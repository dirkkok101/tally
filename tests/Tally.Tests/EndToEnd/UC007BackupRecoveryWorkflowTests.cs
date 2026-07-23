using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Tally.Domain.Ledger;
using Tally.Infrastructure.Artifacts;
using Tally.Infrastructure.Storage;
using Tally.Tests.Process;
using Xunit;

namespace Tally.Tests.EndToEnd;

[SupportedOSPlatform("linux")]
[Collection(PublishedTallyCollection.Name)]
public sealed class UC007BackupRecoveryWorkflowTests(PublishedTallyFixture fixture) : IAsyncLifetime
{
    private const string At = "2026-07-23T00:00:00Z";
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "tally-uc007-" + Guid.NewGuid().ToString("N"));
    private readonly HostArtifactProtection protection = new();
    private string artifactRoot = null!;
    private int sequence;

    [Fact]
    public async Task UC_LEDGER_007_agent_discovers_typed_backup_restore_and_status_contracts()
    {
        foreach (var (operationId, requestType, resultType, mutation) in new[]
                 {
                     ("ledger.backup.create", "CreateBackupInput", "BackupReceipt", true),
                     ("ledger.backup.verify", "VerifyBackupInput", "BackupReceipt", false),
                     ("ledger.restore.prepare", "PrepareRestoreInput", "RestorePrepareResult", true),
                     ("ledger.restore.activate", "ActivateRestoreInput", "RestoreActivationResult", true),
                     ("ledger.storage.status", "StorageStatusInput", "StorageStatusResult", false)
                 })
        {
            var operation = (await Success(["schema", "show", operationId, "--input", "-"], Envelope(new JsonObject()), "system.schema.show")).GetProperty("operation");
            Assert.EndsWith(requestType, operation.GetProperty("requestType").GetString(), StringComparison.Ordinal);
            Assert.EndsWith(resultType, operation.GetProperty("resultType").GetString(), StringComparison.Ordinal);
            Assert.Equal(mutation, operation.GetProperty("requiresIdempotencyKey").GetBoolean());
        }
    }

    [Fact]
    public async Task FR_LEDGER_BACKUP_VERIFICATION_create_publishes_one_owner_only_complete_artifact_without_live_mutation()
    {
        var before = await Status();
        var beforeActuals = await Actuals();
        var target = Target("complete");

        var receipt = await CreateBackup(target);
        var after = await Status();
        var afterActuals = await Actuals();

        Assert.True(File.Exists(target));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(target));
        Assert.Equal(Path.GetFileName(target), receipt.GetProperty("artifactName").GetString());
        Assert.Equal(await Checksum(target), receipt.GetProperty("artifactChecksum").GetString());
        Assert.Equal(before.GetProperty("currentGenerationId").GetString(), after.GetProperty("currentGenerationId").GetString());
        Assert.Equal(beforeActuals.GetProperty("totals").GetRawText(), afterActuals.GetProperty("totals").GetRawText());
        Assert.Equal(beforeActuals.GetProperty("groups").GetRawText(), afterActuals.GetProperty("groups").GetRawText());
        Assert.Equal(beforeActuals.GetProperty("items").GetRawText(), afterActuals.GetProperty("items").GetRawText());
        var manifest = receipt.GetProperty("manifest");
        Assert.Equal(31, manifest.GetProperty("types").GetArrayLength());
        Assert.Equal(5, manifest.GetProperty("actuals").GetArrayLength());
        foreach (var type in new[]
                 {
                     "transaction_fact", "category_parent_event", "category_allocation_event", "pool_assignment_event",
                     "evidence_record", "statement_scope", "statement_scope_evidence", "reconciliation_decision",
                     "statement_correction", "statement_correction_relationship_event", "relationship_lifecycle_event",
                     "coverage_entry", "idempotency_record", "logical_effect"
                 })
            Assert.True(TypeCount(manifest, type) > 0, type);
        Assert.DoesNotContain(manifest.GetProperty("types").EnumerateArray(), type => type.GetProperty("name").GetString()!.StartsWith("query_snapshot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FR_LEDGER_BACKUP_VERIFICATION_verify_recomputes_the_same_normalized_report()
    {
        var target = Target("verify");
        var created = await CreateBackup(target);

        var verified = await VerifyBackup(target, created.GetProperty("artifactChecksum").GetString());

        Assert.Equal(created.GetProperty("artifactChecksum").GetString(), verified.GetProperty("artifactChecksum").GetString());
        Assert.Equal(created.GetProperty("manifest").GetRawText(), verified.GetProperty("manifest").GetRawText());
    }

    [Fact]
    public async Task FR_LEDGER_BACKUP_VERIFICATION_identical_and_cross_key_replay_return_one_artifact_effect()
    {
        var target = Target("replay");
        var request = Envelope(new JsonObject { ["targetPath"] = target }, "uc007-backup-replay");

        var first = await Run(["ledger", "backup", "create", "--input", "-"], request);
        var replay = await Run(["ledger", "backup", "create", "--input", "-"], request);
        var crossKey = await Run(["ledger", "backup", "create", "--input", "-"], Envelope(new JsonObject { ["targetPath"] = target }, "uc007-backup-cross-key"));

        AssertSuccess(first, "ledger.backup.create");
        Assert.Equal(first.Stdout, replay.Stdout);
        Assert.Equal(first.Stdout, crossKey.Stdout);
        Assert.Single(Directory.EnumerateFiles(artifactRoot), path => path == target);
    }

    [Fact]
    public async Task FR_LEDGER_BACKUP_VERIFICATION_existing_unrelated_target_is_not_overwritten()
    {
        var target = Target("existing");
        await File.WriteAllTextAsync(target, "OWNER-CONTENT");
        protection.ProtectArtifact(target);
        var before = await Checksum(target);

        var result = await Run(["ledger", "backup", "create", "--input", "-"], Envelope(new JsonObject { ["targetPath"] = target }, Key("existing")));

        AssertError(result, 5, "LEDGER-BACKUP-TARGET-EXISTS");
        Assert.Equal(before, await Checksum(target));
    }

    [Fact]
    public async Task FR_LEDGER_BACKUP_VERIFICATION_checksum_mismatch_is_read_only()
    {
        var target = Target("checksum");
        await CreateBackup(target);
        var before = await Checksum(target);

        var result = await Run(["ledger", "backup", "verify", "--input", "-"], Envelope(new JsonObject { ["artifactPath"] = target, ["expectedChecksum"] = new string('0', 64) }));

        AssertError(result, 8, "LEDGER-BACKUP-CHECKSUM-MISMATCH");
        Assert.Equal(before, await Checksum(target));
    }

    [Fact]
    public async Task FR_LEDGER_BACKUP_VERIFICATION_corrupt_archive_is_rejected_without_mutation()
    {
        var target = Target("corrupt");
        await CreateBackup(target);
        await using (var stream = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Position = stream.Length / 2;
            stream.WriteByte(0xff);
            stream.Flush(true);
        }
        var beforeCurrent = await Status();
        var beforeChecksum = await Checksum(target);

        var result = await Run(["ledger", "backup", "verify", "--input", "-"], Envelope(new JsonObject { ["artifactPath"] = target, ["expectedChecksum"] = (string?)null }));

        AssertError(result, 8, "LEDGER-BACKUP-INTEGRITY");
        Assert.Equal(beforeChecksum, await Checksum(target));
        Assert.Equal(beforeCurrent.GetProperty("currentGenerationId").GetString(), (await Status()).GetProperty("currentGenerationId").GetString());
    }

    [Fact]
    public async Task NFR_LEDGER_LOCAL_DATA_PROTECTION_unsafe_artifact_permissions_are_rejected()
    {
        var target = Target("permissions");
        await CreateBackup(target);
        var unsafeMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        File.SetUnixFileMode(target, unsafeMode);

        var result = await Run(["ledger", "backup", "verify", "--input", "-"], Envelope(new JsonObject { ["artifactPath"] = target, ["expectedChecksum"] = (string?)null }));

        AssertError(result, 9, "LEDGER-BACKUP-HOST-PROTECTION");
        Assert.Equal(unsafeMode, File.GetUnixFileMode(target));
    }

    [Theory]
    [InlineData("unsupported-schema", "LEDGER-BACKUP-INCOMPATIBLE", 7)]
    [InlineData("cyclic-hierarchy", "LEDGER-BACKUP-INTEGRITY", 8)]
    [InlineData("duplicate-scope", "LEDGER-BACKUP-INTEGRITY", 8)]
    [InlineData("incomplete-correction", "LEDGER-BACKUP-INTEGRITY", 8)]
    [InlineData("invalid-relationship", "LEDGER-BACKUP-INTEGRITY", 8)]
    [InlineData("exact-total", "LEDGER-BACKUP-INTEGRITY", 8)]
    public async Task FR_LEDGER_BACKUP_VERIFICATION_semantic_corruption_is_rejected_by_the_published_verifier(string scenario, string error, int exit)
    {
        var target = Target("semantic-" + scenario);
        await CreateBackup(target);
        await RewriteDatabase(target, connection => Corrupt(connection, scenario));
        var before = await Status();

        var result = await Run(["ledger", "backup", "verify", "--input", "-"], Envelope(new JsonObject { ["artifactPath"] = target, ["expectedChecksum"] = (string?)null }));

        AssertError(result, exit, error);
        Assert.Equal(before.GetProperty("currentGenerationId").GetString(), (await Status()).GetProperty("currentGenerationId").GetString());
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_RESTORE_prepare_creates_a_private_verified_candidate_without_changing_current()
    {
        var target = Target("prepare");
        var backup = await CreateBackup(target);
        var before = await Status();

        var prepared = await Prepare(target, backup.GetProperty("artifactChecksum").GetString()!);
        var after = await Status();

        Assert.NotEqual(before.GetProperty("currentGenerationId").GetString(), prepared.GetProperty("candidateId").GetString());
        Assert.Equal(before.GetProperty("currentGenerationId").GetString(), after.GetProperty("currentGenerationId").GetString());
        Assert.Equal(backup.GetProperty("manifest").GetProperty("normalizedFingerprint").GetString(), prepared.GetProperty("candidateNormalizedFingerprint").GetString());
        Assert.Equal(prepared.GetProperty("sourceNormalizedFingerprint").GetString(), prepared.GetProperty("candidateNormalizedFingerprint").GetString());
        Assert.Equal(backup.GetProperty("manifest").GetProperty("types").GetRawText(), prepared.GetProperty("types").GetRawText());
        Assert.Equal(backup.GetProperty("manifest").GetProperty("actuals").GetRawText(), prepared.GetProperty("actuals").GetRawText());
    }

    [Theory]
    [InlineData("unauthorized", "LEDGER-RESTORE-NOT-AUTHORIZED", 3)]
    [InlineData("stale-current", "LEDGER-RESTORE-STALE-CURRENT", 6)]
    [InlineData("stale-candidate", "LEDGER-RESTORE-STALE-CANDIDATE", 6)]
    public async Task FR_LEDGER_SAFE_RESTORE_activation_failures_preserve_the_current_generation(string scenario, string error, int exit)
    {
        var target = Target("activation-" + scenario);
        var backup = await CreateBackup(target);
        var prepared = await Prepare(target, backup.GetProperty("artifactChecksum").GetString()!);
        var status = await Status();
        var current = status.GetProperty("currentGenerationId").GetString();
        var currentFingerprint = status.GetProperty("currentNormalizedFingerprint").GetString()!;
        var candidateFingerprint = prepared.GetProperty("candidateNormalizedFingerprint").GetString()!;
        if (scenario == "stale-current") currentFingerprint = new string('0', 64);
        if (scenario == "stale-candidate") candidateFingerprint = new string('0', 64);

        var result = await Run(["ledger", "restore", "activate", "--input", "-"], Envelope(new JsonObject
        {
            ["candidateId"] = prepared.GetProperty("candidateId").GetString(),
            ["expectedCurrentFingerprint"] = currentFingerprint,
            ["expectedCandidateFingerprint"] = candidateFingerprint,
            ["authorizeReplacement"] = scenario != "unauthorized"
        }, Key("activate-failure")));

        AssertError(result, exit, error);
        Assert.Equal(current, (await Status()).GetProperty("currentGenerationId").GetString());
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_RESTORE_activation_is_atomic_and_retains_the_prior_generation()
    {
        var target = Target("activate");
        var backup = await CreateBackup(target);
        var prepared = await Prepare(target, backup.GetProperty("artifactChecksum").GetString()!);
        var before = await Status();
        var prior = before.GetProperty("currentGenerationId").GetString()!;

        var activated = await Success(["ledger", "restore", "activate", "--input", "-"], Envelope(new JsonObject
        {
            ["candidateId"] = prepared.GetProperty("candidateId").GetString(),
            ["expectedCurrentFingerprint"] = before.GetProperty("currentNormalizedFingerprint").GetString(),
            ["expectedCandidateFingerprint"] = prepared.GetProperty("candidateNormalizedFingerprint").GetString(),
            ["authorizeReplacement"] = true
        }, Key("activate")), "ledger.restore.activate");

        Assert.Equal(prepared.GetProperty("candidateId").GetString(), activated.GetProperty("currentGenerationId").GetString());
        Assert.Equal(activated.GetProperty("currentGenerationId").GetString(), (await Status()).GetProperty("currentGenerationId").GetString());
        Assert.True(Directory.Exists(Path.Combine(dataRoot, "generations", prior)));
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_RESTORE_corrupt_source_fails_before_candidate_or_activation()
    {
        var target = Target("restore-corrupt");
        var backup = await CreateBackup(target);
        await using (var stream = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Position = stream.Length / 2;
            stream.WriteByte(0xff);
            stream.Flush(true);
        }
        var before = await Status();

        var result = await Run(["ledger", "restore", "prepare", "--input", "-"], Envelope(new JsonObject
        {
            ["artifactPath"] = target,
            ["expectedArtifactChecksum"] = backup.GetProperty("artifactChecksum").GetString()
        }, Key("prepare-corrupt")));

        AssertError(result, 8, "LEDGER-RESTORE-INTEGRITY");
        Assert.Equal(before.GetProperty("currentGenerationId").GetString(), (await Status()).GetProperty("currentGenerationId").GetString());
    }

    [Fact]
    public async Task NFR_LEDGER_LOCAL_DATA_PROTECTION_recovery_failures_do_not_echo_private_payloads_or_paths()
    {
        const string canary = "PRIVATE-STATEMENT-CANARY";
        var privatePath = Path.Combine(artifactRoot, canary + ".tally-backup");

        var result = await Run(["ledger", "backup", "verify", "--input", "-"], Envelope(new JsonObject { ["artifactPath"] = privatePath, ["expectedChecksum"] = (string?)null }));

        AssertError(result, 4, "LEDGER-BACKUP-NOT-FOUND");
        Assert.DoesNotContain(canary, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(artifactRoot, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(artifactRoot, result.Stderr, StringComparison.Ordinal);
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(dataRoot);
        protection.EnsureDataRoot(dataRoot);
        artifactRoot = Path.Combine(Path.GetTempPath(), "tally-uc007-artifacts-" + Guid.NewGuid().ToString("N"));
        protection.EnsureDataRoot(artifactRoot);
        await SeedPublicState();
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
        if (Directory.Exists(artifactRoot)) Directory.Delete(artifactRoot, true);
        return Task.CompletedTask;
    }

    private async Task SeedPublicState()
    {
        var accountId = (await Success(["ledger", "account", "create", "--input", "-"], Envelope(new JsonObject
        {
            ["institutionName"] = "Example Bank",
            ["displayName"] = "Recovery account",
            ["accountType"] = "cheque",
            ["maskedIdentifier"] = "****7007",
            ["currencyCode"] = "ZAR"
        }, Key("account")), "ledger.account.create")).GetProperty("accountId").GetString()!;
        var counterpartAccountId = (await Success(["ledger", "account", "create", "--input", "-"], Envelope(new JsonObject
        {
            ["institutionName"] = "Example Bank",
            ["displayName"] = "Recovery counterpart account",
            ["accountType"] = "savings",
            ["maskedIdentifier"] = "****7008",
            ["currencyCode"] = "ZAR"
        }, Key("counterpart-account")), "ledger.account.create")).GetProperty("accountId").GetString()!;
        var parentCategoryId = (await Success(["ledger", "category", "create", "--input", "-"], Envelope(new JsonObject { ["name"] = "Recovery parent category", ["parentCategoryId"] = null }, Key("parent-category")), "ledger.category.create")).GetProperty("categoryId").GetString()!;
        var categoryId = (await Success(["ledger", "category", "create", "--input", "-"], Envelope(new JsonObject { ["name"] = "Recovery category", ["parentCategoryId"] = parentCategoryId }, Key("category")), "ledger.category.create")).GetProperty("categoryId").GetString()!;
        var poolId = (await Success(["ledger", "pool", "create", "--input", "-"], Envelope(new JsonObject { ["name"] = "Recovery pool" }, Key("pool")), "ledger.pool.create")).GetProperty("poolId").GetString()!;
        var exactTransaction = await Record(accountId, "-12.34", "2026-07-10", "Recovery exact transaction");
        var exactTransactionId = exactTransaction.GetProperty("transactionId").GetString()!;
        await AssignCategory(exactTransactionId, categoryId, "exact");
        await AssignPool(exactTransaction, poolId, "exact");
        var correctedTransaction = await Record(accountId, "-25.00", "2026-07-11", "Recovery notification transaction");
        var correctedTransactionId = correctedTransaction.GetProperty("transactionId").GetString()!;
        await AssignCategory(correctedTransactionId, categoryId, "corrected");
        await AssignPool(correctedTransaction, poolId, "corrected");
        var counterpartTransaction = await Record(counterpartAccountId, "25.00", "2026-07-11", "Recovery transfer counterpart");
        var counterpartTransactionId = counterpartTransaction.GetProperty("transactionId").GetString()!;
        await Success(["ledger", "transfer", "confirm", "--input", "-"], Envelope(new JsonObject
        {
            ["outflowTransactionId"] = correctedTransactionId,
            ["inflowTransactionId"] = counterpartTransactionId,
            ["reason"] = "Owner confirmed recovery transfer"
        }, Key("transfer")), "ledger.transfer.confirm");
        var exactEvidenceId = await RegisterStatementEvidence(accountId, -1234, "2026-07-10");
        var correctedEvidenceId = await RegisterStatementEvidence(accountId, -2500, "2026-07-11");
        var scope = await Success(["ledger", "reconciliation", "scope", "register", "--input", "-"], Envelope(new JsonObject
        {
            ["accountId"] = accountId,
            ["periodStart"] = "2026-07-01",
            ["periodEnd"] = "2026-07-31",
            ["manifestOpaqueReference"] = "statement:recovery",
            ["evidenceIds"] = Array(exactEvidenceId, correctedEvidenceId)
        }, Key("scope")), "ledger.reconciliation.scope.register");
        var scopeId = scope.GetProperty("scopeId").GetString()!;
        var exactProjection = await Success(["ledger", "reconciliation", "candidates", "--input", "-"], Envelope(new JsonObject
        {
            ["evidenceId"] = exactEvidenceId,
            ["scopeId"] = scopeId,
            ["policyId"] = "manual_review_projection",
            ["policyVersion"] = "1.0"
        }), "ledger.reconciliation.candidates");
        var exactCandidates = CandidateIds(exactProjection);
        await Success(["ledger", "reconciliation", "apply", "--input", "-"], Envelope(new JsonObject
        {
            ["evidenceId"] = exactEvidenceId,
            ["evidenceFingerprint"] = exactProjection.GetProperty("evidenceFingerprint").GetString(),
            ["scopeId"] = scopeId,
            ["expectedProjectionToken"] = exactProjection.GetProperty("advisoryToken").GetString(),
            ["disposition"] = "match_existing",
            ["authorityKind"] = "owner",
            ["reviewedCandidateIds"] = new JsonArray(exactCandidates.Select(value => JsonValue.Create(value)).ToArray()),
            ["targetTransactionId"] = exactTransactionId,
            ["statementFact"] = null,
            ["exceptionCode"] = null,
            ["reason"] = "Owner confirmed statement"
        }, Key("apply-exact")), "ledger.reconciliation.apply");
        var correctedProjection = await Success(["ledger", "reconciliation", "candidates", "--input", "-"], Envelope(new JsonObject
        {
            ["evidenceId"] = correctedEvidenceId,
            ["scopeId"] = scopeId,
            ["policyId"] = "manual_review_projection",
            ["policyVersion"] = "1.0"
        }), "ledger.reconciliation.candidates");
        var correctedCandidates = CandidateIds(correctedProjection);
        var correction = await Success(["ledger", "reconciliation", "apply", "--input", "-"], Envelope(new JsonObject
        {
            ["evidenceId"] = correctedEvidenceId,
            ["evidenceFingerprint"] = correctedProjection.GetProperty("evidenceFingerprint").GetString(),
            ["scopeId"] = scopeId,
            ["expectedProjectionToken"] = correctedProjection.GetProperty("advisoryToken").GetString(),
            ["disposition"] = "correct_existing_from_statement",
            ["authorityKind"] = "owner",
            ["reviewedCandidateIds"] = new JsonArray(correctedCandidates.Select(value => JsonValue.Create(value)).ToArray()),
            ["targetTransactionId"] = correctedTransactionId,
            ["statementFact"] = new JsonObject
            {
                ["accountId"] = accountId,
                ["signedAmount"] = "-25.00",
                ["currencyCode"] = "ZAR",
                ["transactionDate"] = "2026-07-11",
                ["postingDate"] = null,
                ["originalDescription"] = "Authoritative recovery statement row"
            },
            ["exceptionCode"] = null,
            ["reason"] = "Owner approved statement correction"
        }, Key("apply-correction")), "ledger.reconciliation.apply");
        Assert.Single(correction.GetProperty("correction").GetProperty("relationshipLifecycleEventIds").EnumerateArray());
        var coverage = await Success(["ledger", "reconciliation", "coverage", "complete", "--input", "-"], Envelope(new JsonObject
        {
            ["scopeId"] = scopeId,
            ["accountId"] = accountId,
            ["periodStart"] = "2026-07-01",
            ["periodEnd"] = "2026-07-31",
            ["manifestOpaqueReference"] = "statement:recovery",
            ["expectedEvidenceIds"] = Array(exactEvidenceId, correctedEvidenceId),
            ["policyId"] = "statement-coverage-v1",
            ["policyVersion"] = "1.0"
        }, Key("coverage")), "ledger.reconciliation.coverage.complete");
        Assert.Equal(2, coverage.GetProperty("evidenceCount").GetInt32());
        Assert.Contains(coverage.GetProperty("currentMembers").EnumerateArray(), member => member.GetProperty("outcome").GetString() == "corrected_from_statement");
        await Success(["ledger", "actuals", "query", "--input", "-"], Envelope(new JsonObject { ["filter"] = new JsonObject { ["accountIds"] = Array(accountId), ["groupBy"] = "pool_category" }, ["pageSize"] = 1, ["cursor"] = null }), "ledger.actuals.query");
    }

    private async Task AssignCategory(string transactionId, string categoryId, string purpose) =>
        _ = await Success(["ledger", "transaction", "category", "assign", "--input", "-"], Envelope(new JsonObject
        {
            ["transactionId"] = transactionId,
            ["categoryId"] = categoryId,
            ["reason"] = "Owner classification"
        }, Key("assign-category-" + purpose)), "ledger.transaction.category.assign");

    private async Task AssignPool(JsonElement transaction, string poolId, string purpose) =>
        _ = await Success(["ledger", "transaction", "pool", "assign", "--input", "-"], Envelope(new JsonObject
        {
            ["transactionId"] = transaction.GetProperty("transactionId").GetString(),
            ["expectedPoolAssignmentEventId"] = transaction.GetProperty("pool").GetProperty("poolAssignmentEventId").GetString(),
            ["assignment"] = new JsonObject { ["state"] = "assigned", ["poolId"] = poolId },
            ["reason"] = "Owner pool"
        }, Key("assign-pool-" + purpose)), "ledger.transaction.pool.assign");

    private static string[] CandidateIds(JsonElement projection) =>
        projection.GetProperty("exactCandidates").EnumerateArray()
            .Concat(projection.GetProperty("guardCandidates").EnumerateArray())
            .Select(candidate => candidate.GetProperty("transactionId").GetString()!)
            .ToArray();

    private async Task<JsonElement> Record(string accountId, string amount, string date, string description)
    {
        var token = Key("record");
        return await Success(["ledger", "transaction", "record", "--input", "-"], Envelope(new JsonObject
        {
            ["accountId"] = accountId,
            ["signedAmount"] = amount,
            ["currencyCode"] = "ZAR",
            ["transactionDate"] = date,
            ["postingDate"] = null,
            ["originalDescription"] = description,
            ["instrumentId"] = null,
            ["cardholderId"] = null,
            ["initialEvidence"] = new JsonObject { ["kind"] = "agent_capture", ["logicalIdentityDigest"] = Digest(token), ["opaqueExternalReference"] = "capture:" + token, ["contentFingerprint"] = null, ["observation"] = null }
        }, token), "ledger.transaction.record");
    }

    private async Task<string> RegisterStatementEvidence(string accountId, long amountMinor, string date)
    {
        var token = Key("statement");
        return (await Success(["ledger", "evidence", "register", "--input", "-"], Envelope(new JsonObject
        {
            ["kind"] = "statement_row",
            ["logicalIdentityDigest"] = Digest(token),
            ["opaqueExternalReference"] = "statement:" + token,
            ["contentFingerprint"] = Digest("content-" + token),
            ["observation"] = new JsonObject { ["accountId"] = accountId, ["signedAmountMinor"] = amountMinor, ["currencyCode"] = "ZAR", ["transactionDate"] = date, ["postingDate"] = null, ["instrumentId"] = null, ["cardholderId"] = null, ["descriptionFingerprint"] = Digest("description-" + token) }
        }, token), "ledger.evidence.register")).GetProperty("evidenceId").GetString()!;
    }

    private string Target(string name) => Path.Combine(artifactRoot, name + "-" + Interlocked.Increment(ref sequence) + ".tally-backup");

    private async Task<JsonElement> CreateBackup(string target) => await Success(["ledger", "backup", "create", "--input", "-"], Envelope(new JsonObject { ["targetPath"] = target }, Key("backup")), "ledger.backup.create");

    private async Task<JsonElement> VerifyBackup(string target, string? checksum) => await Success(["ledger", "backup", "verify", "--input", "-"], Envelope(new JsonObject { ["artifactPath"] = target, ["expectedChecksum"] = checksum }), "ledger.backup.verify");

    private async Task<JsonElement> Prepare(string target, string checksum) => await Success(["ledger", "restore", "prepare", "--input", "-"], Envelope(new JsonObject { ["artifactPath"] = target, ["expectedArtifactChecksum"] = checksum }, Key("prepare")), "ledger.restore.prepare");

    private async Task<JsonElement> Status() => await Success(["ledger", "storage", "status", "--input", "-"], Envelope(new JsonObject()), "ledger.storage.status");

    private async Task<JsonElement> Actuals() => await Success(["ledger", "actuals", "query", "--input", "-"], Envelope(new JsonObject { ["filter"] = new JsonObject { ["groupBy"] = "pool_category" }, ["pageSize"] = 100, ["cursor"] = null }), "ledger.actuals.query");

    private static long TypeCount(JsonElement manifest, string name) => manifest.GetProperty("types").EnumerateArray().Single(type => type.GetProperty("name").GetString() == name).GetProperty("rowCount").GetInt64();

    private async Task RewriteDatabase(string artifactPath, Func<SqliteConnection, Task> rewrite)
    {
        var directory = Path.Combine(artifactRoot, "rewrite-" + Guid.NewGuid().ToString("N"));
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

    private static async Task Corrupt(SqliteConnection connection, string scenario)
    {
        switch (scenario)
        {
            case "unsupported-schema":
                await Execute(connection, $"PRAGMA user_version = {CompleteLedgerSchema.CurrentVersion + 1};");
                break;
            case "cyclic-hierarchy":
                {
                    var first = LedgerId.New().ToString();
                    var second = LedgerId.New().ToString();
                    await Execute(connection, "DROP TRIGGER category_parent_cycle_before_insert; DROP TRIGGER category_parent_requires_active_nodes_before_insert;");
                    await Execute(connection, $"INSERT INTO spend_category VALUES ('{first}', '{At}'), ('{second}', '{At}'); INSERT INTO category_parent_event VALUES ('{LedgerId.New()}', '{first}', '{second}', 'initialize', 'bad', 'test', '{At}', NULL), ('{LedgerId.New()}', '{second}', '{first}', 'initialize', 'bad', 'test', '{At}', NULL);");
                    break;
                }
            case "duplicate-scope":
                {
                    var scope = LedgerId.New().ToString();
                    await Execute(connection, $"INSERT INTO statement_scope SELECT '{scope}', account_id, period_start, period_end, manifest_opaque_reference || ':duplicate', status, created_by, created_at FROM statement_scope LIMIT 1; INSERT INTO statement_scope_evidence SELECT '{scope}', evidence_id FROM statement_scope_evidence LIMIT 1;");
                    break;
                }
            case "incomplete-correction":
                await Execute(connection, "DROP TRIGGER reconciliation_decision_authority_is_immutable_before_update; UPDATE reconciliation_decision_authority SET disposition_detail = 'corrected_from_statement' WHERE decision_id = (SELECT decision_id FROM reconciliation_decision_authority LIMIT 1);");
                break;
            case "invalid-relationship":
                {
                    var account = await ScalarText(connection, "SELECT account_id FROM account LIMIT 1;");
                    var first = LedgerId.New().ToString();
                    var second = LedgerId.New().ToString();
                    await Execute(connection, $"INSERT INTO transaction_fact VALUES ('{first}', '{account}', -1000, 'ZAR', '2026-07-20', NULL, 'first', '{At}', 'test'), ('{second}', '{account}', 900, 'ZAR', '2026-07-20', NULL, 'second', '{At}', 'test'); INSERT INTO transaction_attribution_event VALUES ('{LedgerId.New()}', '{first}', 'unknown', NULL, 'unknown', NULL, 'initialize', NULL, NULL, NULL, 'initial', 'test', '{At}'), ('{LedgerId.New()}', '{second}', 'unknown', NULL, 'unknown', NULL, 'initialize', NULL, NULL, NULL, 'initial', 'test', '{At}'); INSERT INTO pool_assignment_event VALUES ('{LedgerId.New()}', '{first}', 'unassigned', NULL, 'initialize', NULL, NULL, NULL, 'initial', 'test', '{At}'), ('{LedgerId.New()}', '{second}', 'unassigned', NULL, 'initialize', NULL, NULL, NULL, 'initial', 'test', '{At}'); INSERT INTO financial_relationship VALUES ('{LedgerId.New()}', 'refund', '{first}', 'refund_original', '{second}', 'refund_credit', 1000, 'active', '{At}', 'test', NULL);");
                    break;
                }
            case "exact-total":
                await Execute(connection, "PRAGMA foreign_keys = OFF; DROP TRIGGER pool_assignment_is_immutable_before_delete; DELETE FROM pool_assignment_event WHERE rowid = (SELECT rowid FROM pool_assignment_event LIMIT 1);");
                break;
            default:
                throw new InvalidOperationException(scenario);
        }
    }

    private static async Task Execute(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ScalarText(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private Task<PublishedTallyResult> Run(IReadOnlyList<string> arguments, string? input = null) => fixture.RunAsync(dataRoot, arguments, input);

    private async Task<JsonElement> Success(IReadOnlyList<string> arguments, string input, string operationId) => AssertSuccess(await Run(arguments, input), operationId);

    private string Key(string purpose) => "uc007-" + purpose + "-" + Interlocked.Increment(ref sequence);

    private static JsonArray Array(params string[] values) => new(values.Select(value => JsonValue.Create(value)).ToArray());

    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static async Task<string> Checksum(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }

    private static string Envelope(JsonNode input, string? idempotencyKey = null)
    {
        var envelope = new JsonObject { ["contractVersion"] = "1.0", ["actor"] = new JsonObject { ["kind"] = "automation", ["label"] = "uc007", ["runId"] = "published-e2e" }, ["input"] = input };
        if (idempotencyKey is not null) envelope["idempotencyKey"] = idempotencyKey;
        return envelope.ToJsonString();
    }

    private static JsonElement AssertSuccess(PublishedTallyResult result, string operationId)
    {
        Assert.True(result.ExitCode == 0, $"Exit {result.ExitCode}: {result.Stdout} {result.Stderr}");
        Assert.Equal(string.Empty, result.Stderr);
        Assert.Single(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(operationId, document.RootElement.GetProperty("operationId").GetString());
        Assert.Equal("success", document.RootElement.GetProperty("outcome").GetString());
        return document.RootElement.GetProperty("result").Clone();
    }

    private static void AssertError(PublishedTallyResult result, int exitCode, string errorCode)
    {
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal("tally: " + errorCode, result.Stderr);
        Assert.Single(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(errorCode, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
