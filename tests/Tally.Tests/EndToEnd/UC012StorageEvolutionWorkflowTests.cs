using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tally.Infrastructure.Artifacts;
using Tally.Infrastructure.Storage;
using Tally.Tests.Process;
using Xunit;

namespace Tally.Tests.EndToEnd;

[SupportedOSPlatform("linux")]
[Collection(PublishedTallyCollection.Name)]
public sealed class UC012StorageEvolutionWorkflowTests(PublishedTallyFixture fixture) : IAsyncLifetime
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "tally-uc012-" + Guid.NewGuid().ToString("N"));
    private string sourceGenerationId = null!;
    private int sequence;

    [Fact]
    public async Task UC_LEDGER_012_agent_discovers_status_prepare_and_activate_contracts()
    {
        foreach (var (operationId, requestType, resultType, mutation) in new[]
                 {
                     ("ledger.storage.status", "StorageStatusInput", "StorageStatusResult", false),
                     ("ledger.storage.evolution.prepare", "PrepareStorageEvolutionInput", "StorageEvolutionPrepareResult", true),
                     ("ledger.storage.evolution.activate", "ActivateStorageEvolutionInput", "StorageEvolutionActivationResult", true)
                 })
        {
            var operation = (await Success(
                ["schema", "show", operationId, "--input", "-"],
                Envelope(new JsonObject()),
                "system.schema.show")).GetProperty("operation");
            Assert.EndsWith(requestType, operation.GetProperty("requestType").GetString(), StringComparison.Ordinal);
            Assert.EndsWith(resultType, operation.GetProperty("resultType").GetString(), StringComparison.Ordinal);
            Assert.Equal(mutation, operation.GetProperty("requiresIdempotencyKey").GetBoolean());
        }
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_status_reports_verified_legacy_source_without_mutation()
    {
        var before = await CurrentId();

        var status = await Status();

        Assert.Equal(1, status.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(CompleteLedgerSchema.CurrentVersion, status.GetProperty("currentSchemaVersion").GetInt32());
        Assert.Equal(sourceGenerationId, status.GetProperty("currentGenerationId").GetString());
        AssertFingerprint(status.GetProperty("currentFingerprint").GetString());
        AssertFingerprint(status.GetProperty("currentNormalizedFingerprint").GetString());
        Assert.True(status.GetProperty("ownerOnlyPermissions").GetBoolean());
        Assert.True(status.GetProperty("integrityVerified").GetBoolean());
        Assert.True(status.GetProperty("hostProtectionVerified").GetBoolean());
        Assert.Equal(before, await CurrentId());
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_prepare_isolated_candidate_reports_complete_state_equivalence()
    {
        var sourceStatus = await Status();

        var prepared = await Prepare("complete-state");

        Assert.Equal(sourceGenerationId, await CurrentId());
        Assert.NotEqual(sourceGenerationId, prepared.GetProperty("candidateId").GetString());
        Assert.Equal(1, prepared.GetProperty("sourceSchemaVersion").GetInt32());
        Assert.Equal(CompleteLedgerSchema.CurrentVersion, prepared.GetProperty("targetSchemaVersion").GetInt32());
        Assert.Equal(sourceStatus.GetProperty("currentFingerprint").GetString(), prepared.GetProperty("sourceFingerprint").GetString());
        Assert.Equal("2", prepared.GetProperty("storageContractVersion").GetString());
        Assert.NotEmpty(prepared.GetProperty("reconciliationPolicyVersions").EnumerateArray());
        Assert.Equal(31, prepared.GetProperty("types").GetArrayLength());
        Assert.Equal(5, prepared.GetProperty("actuals").GetArrayLength());
        foreach (var name in new[]
                 {
                     "account", "cardholder", "spend_category", "spend_pool", "payment_instrument",
                     "transaction_fact", "transaction_lifecycle_event", "category_allocation_event",
                     "pool_assignment_event", "transaction_attribution_event", "evidence_record",
                     "evidence_link_event", "financial_relationship", "relationship_lifecycle_event",
                     "idempotency_record", "logical_effect", "statement_scope", "statement_correction"
                 })
            Assert.Contains(prepared.GetProperty("types").EnumerateArray(), type => type.GetProperty("name").GetString() == name);
        Assert.All(prepared.GetProperty("types").EnumerateArray(), type => AssertFingerprint(type.GetProperty("fingerprint").GetString()));
        Assert.All(prepared.GetProperty("actuals").EnumerateArray(), actual =>
        {
            Assert.Equal(0, actual.GetProperty("memberCount").GetInt64());
            Assert.Equal(0, actual.GetProperty("netAccountMovementMinor").GetInt64());
            Assert.Equal(0, actual.GetProperty("externalSpendMinor").GetInt64());
            Assert.Equal(0, actual.GetProperty("budgetActualMinor").GetInt64());
            AssertFingerprint(actual.GetProperty("cellFingerprint").GetString());
        });
        foreach (var property in new[]
                 {
                     "candidateNormalizedFingerprint", "categoryHierarchyFingerprint", "transactionReplacementFingerprint",
                     "relationshipFingerprint", "reconciliationFingerprint", "idempotencyFingerprint"
                 })
            AssertFingerprint(prepared.GetProperty(property).GetString());
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_prepare_creates_independently_verifiable_recovery_artifact()
    {
        var prepared = await Prepare("recovery-point");
        var artifactPath = Path.Combine(dataRoot, "recovery-artifacts", prepared.GetProperty("candidateId").GetString()! + ".tally-backup");

        var verified = await Success(
            ["ledger", "backup", "verify", "--input", "-"],
            Envelope(new JsonObject
            {
                ["artifactPath"] = artifactPath,
                ["expectedChecksum"] = prepared.GetProperty("recoveryArtifactChecksum").GetString()
            }),
            "ledger.backup.verify");

        Assert.Equal(prepared.GetProperty("recoveryArtifactChecksum").GetString(), verified.GetProperty("artifactChecksum").GetString());
        Assert.Equal(prepared.GetProperty("candidateNormalizedFingerprint").GetString(), verified.GetProperty("manifest").GetProperty("normalizedFingerprint").GetString());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(artifactPath));
        Assert.True(Directory.Exists(Path.Combine(dataRoot, "recovery", "generations", prepared.GetProperty("recoveryGenerationId").GetString()!)));
    }

    [Fact]
    public async Task FR_LEDGER_IDEMPOTENT_WRITES_prepare_exact_and_cross_key_replay_converge_while_changed_actor_conflicts()
    {
        var request = PrepareEnvelope("prepare-replay", "uc012");

        var first = await Run(["ledger", "storage", "evolution", "prepare", "--input", "-"], request);
        var replay = await Run(["ledger", "storage", "evolution", "prepare", "--input", "-"], request);
        var crossKey = await Run(["ledger", "storage", "evolution", "prepare", "--input", "-"], PrepareEnvelope("prepare-cross-key", "uc012"));
        var changedActor = await Run(["ledger", "storage", "evolution", "prepare", "--input", "-"], PrepareEnvelope("prepare-replay", "different-actor"));

        AssertSuccess(first, "ledger.storage.evolution.prepare");
        Assert.Equal(first.Stdout, replay.Stdout);
        Assert.Equal(first.Stdout, crossKey.Stdout);
        AssertError(changedActor, 5, "LEDGER-IDEMPOTENCY-001");
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(dataRoot, "generations")), path => Path.GetFileName(path) != sourceGenerationId);
    }

    [Theory]
    [InlineData("missing-key", 3, "validation.invalid_input")]
    [InlineData("unsupported-target", 3, "LEDGER-STORAGE-EVOLUTION-INVALID")]
    public async Task UC_LEDGER_012_invalid_prepare_never_creates_or_activates_candidate(string scenario, int exitCode, string errorCode)
    {
        var target = scenario == "unsupported-target" ? CompleteLedgerSchema.CurrentVersion + 1 : CompleteLedgerSchema.CurrentVersion;
        var key = scenario == "missing-key" ? null : "unsupported-target";

        AssertError(
            await Run(
                ["ledger", "storage", "evolution", "prepare", "--input", "-"],
                Envelope(new JsonObject { ["targetSchemaVersion"] = target }, key)),
            exitCode,
            errorCode);
        Assert.Equal(sourceGenerationId, await CurrentId());
        Assert.DoesNotContain(Directory.EnumerateDirectories(Path.Combine(dataRoot, "generations")), path => Path.GetFileName(path) != sourceGenerationId);
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_activation_requires_explicit_authorization()
    {
        var prepared = await Prepare("unauthorized-prepare");

        AssertError(await ActivateResult(prepared, false, "unauthorized"), 3, "LEDGER-STORAGE-EVOLUTION-NOT-AUTHORIZED");
        Assert.Equal(sourceGenerationId, await CurrentId());
    }

    [Theory]
    [InlineData("current", "LEDGER-STORAGE-EVOLUTION-STALE-CURRENT")]
    [InlineData("candidate", "LEDGER-STORAGE-EVOLUTION-STALE-CANDIDATE")]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_stale_fingerprint_never_replaces_current(string stale, string errorCode)
    {
        var prepared = await Prepare("stale-prepare");
        var input = ActivationInput(prepared, true);
        input[stale == "current" ? "expectedCurrentFingerprint" : "expectedCandidateFingerprint"] = new string('0', 64);

        AssertError(
            await Run(["ledger", "storage", "evolution", "activate", "--input", "-"], Envelope(input, "stale-" + stale)),
            6,
            errorCode);
        Assert.Equal(sourceGenerationId, await CurrentId());
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_tampered_candidate_is_revalidated_and_rejected()
    {
        var prepared = await Prepare("tamper-prepare");
        var candidatePath = Path.Combine(dataRoot, "generations", prepared.GetProperty("candidateId").GetString()!, "ledger.db");
        await using (var stream = new FileStream(candidatePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = 0;
            var value = stream.ReadByte();
            stream.Position = 0;
            stream.WriteByte((byte)(value ^ 0xff));
            stream.Flush(true);
        }

        AssertError(await ActivateResult(prepared, true, "tampered-candidate"), 6, "LEDGER-STORAGE-EVOLUTION-STALE-CANDIDATE");
        Assert.Equal(sourceGenerationId, await CurrentId());
    }

    [Fact]
    public async Task NFR_LEDGER_LOCAL_DATA_PROTECTION_privacy_invalid_candidate_never_activates()
    {
        var prepared = await Prepare("permissions-prepare");
        var candidatePath = Path.Combine(dataRoot, "generations", prepared.GetProperty("candidateId").GetString()!, "ledger.db");
        File.SetUnixFileMode(candidatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        AssertError(await ActivateResult(prepared, true, "unsafe-candidate"), 6, "LEDGER-STORAGE-EVOLUTION-STALE-CANDIDATE");
        Assert.Equal(sourceGenerationId, await CurrentId());
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_source_change_after_prepare_invalidates_candidate()
    {
        var prepared = await Prepare("source-change-prepare");
        await CreateAccount("Changed source", "cheque");
        var refreshed = await Status();
        var input = ActivationInput(prepared, true);
        input["expectedCurrentFingerprint"] = refreshed.GetProperty("currentFingerprint").GetString();

        AssertError(
            await Run(["ledger", "storage", "evolution", "activate", "--input", "-"], Envelope(input, "source-changed")),
            6,
            "LEDGER-STORAGE-EVOLUTION-STALE-CURRENT");
        Assert.Equal(sourceGenerationId, await CurrentId());
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_authorized_activation_switches_once_and_retains_recovery_generations()
    {
        var prepared = await Prepare("activate-prepare");

        var activated = AssertSuccess(await ActivateResult(prepared, true, "activate"), "ledger.storage.evolution.activate");

        Assert.Equal(prepared.GetProperty("candidateId").GetString(), activated.GetProperty("currentGenerationId").GetString());
        Assert.Equal(prepared.GetProperty("candidateId").GetString(), await CurrentId());
        Assert.Equal(prepared.GetProperty("candidateNormalizedFingerprint").GetString(), activated.GetProperty("normalizedFingerprint").GetString());
        Assert.True(Directory.Exists(Path.Combine(dataRoot, "generations", sourceGenerationId)));
        Assert.True(Directory.Exists(Path.Combine(dataRoot, "recovery", "generations", prepared.GetProperty("recoveryGenerationId").GetString()!)));
        var status = await Status();
        Assert.Equal(CompleteLedgerSchema.CurrentVersion, status.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(activated.GetProperty("currentGenerationId").GetString(), status.GetProperty("currentGenerationId").GetString());
        var afterActuals = await Actuals();
        Assert.Equal("0", afterActuals.GetProperty("totals").GetProperty("netAccountMovement").GetString());
        Assert.Equal("0", afterActuals.GetProperty("totals").GetProperty("externalSpend").GetString());
        Assert.Equal("0", afterActuals.GetProperty("totals").GetProperty("budgetActual").GetString());
        Assert.Empty(afterActuals.GetProperty("groups").EnumerateArray());
        Assert.Empty(afterActuals.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task FR_LEDGER_SAFE_STORAGE_EVOLUTION_new_current_rejects_a_second_evolution()
    {
        var prepared = await Prepare("first-evolution");
        AssertSuccess(await ActivateResult(prepared, true, "first-activation"), "ledger.storage.evolution.activate");

        AssertError(
            await Run(["ledger", "storage", "evolution", "prepare", "--input", "-"], PrepareEnvelope("second-evolution", "uc012")),
            6,
            "LEDGER-STORAGE-EVOLUTION-ALREADY-CURRENT");
        Assert.Equal(prepared.GetProperty("candidateId").GetString(), await CurrentId());
    }

    [Fact]
    public async Task UC_LEDGER_012_interrupted_prepare_keeps_source_current_and_fails_closed_on_incomplete_candidate()
    {
        var request = PrepareEnvelope("crash-prepare", "uc012");

        Assert.True(
            await KillPublishedProcessDuringMutation(["ledger", "storage", "evolution", "prepare", "--input", "-"], request),
            "The published process completed before the interruption could be injected.");
        Assert.Equal(sourceGenerationId, await CurrentId());

        var retry = await Run(["ledger", "storage", "evolution", "prepare", "--input", "-"], request);
        if (retry.ExitCode == 0)
        {
            var prepared = AssertSuccess(retry, "ledger.storage.evolution.prepare");
            Assert.True(Directory.Exists(Path.Combine(dataRoot, "generations", prepared.GetProperty("candidateId").GetString()!)));
        }
        else
        {
            AssertError(retry, 5, "LEDGER-STORAGE-EVOLUTION-CANDIDATE-CONFLICT");
        }
        Assert.Equal(sourceGenerationId, await CurrentId());
    }

    [Fact]
    public async Task UC_LEDGER_012_interrupted_activation_exposes_old_or_complete_new_and_retry_converges()
    {
        var prepared = await Prepare("crash-activation-prepare");
        var request = Envelope(ActivationInput(prepared, true), "crash-activation");

        Assert.True(
            await KillPublishedProcessDuringMutation(["ledger", "storage", "evolution", "activate", "--input", "-"], request),
            "The published process completed before the interruption could be injected.");
        Assert.Contains(await CurrentId(), new[] { sourceGenerationId, prepared.GetProperty("candidateId").GetString()! });

        var activated = AssertSuccess(
            await Run(["ledger", "storage", "evolution", "activate", "--input", "-"], request),
            "ledger.storage.evolution.activate");
        Assert.Equal(prepared.GetProperty("candidateId").GetString(), activated.GetProperty("currentGenerationId").GetString());
        Assert.Equal(prepared.GetProperty("candidateId").GetString(), await CurrentId());
    }

    public async Task InitializeAsync()
    {
        var protection = new HostArtifactProtection();
        protection.EnsureDataRoot(dataRoot);
        sourceGenerationId = Guid.NewGuid().ToString("N");
        var source = new LedgerDb(dataRoot, sourceGenerationId);
        await using (var connection = await new LedgerConnectionFactory(protection).OpenAsync(source, CompleteLedgerSchema.CurrentVersion, CancellationToken.None))
        {
            await CompleteLedgerSchema.CreateV1().ApplyAsync(connection, CancellationToken.None);
        }
        await File.WriteAllTextAsync(source.ManifestPath, "synthetic-legacy-v1");
        protection.ProtectArtifact(source.ManifestPath);
        var manager = new StoreGenerationManager(protection);
        manager.ConfigureDataRoot(dataRoot);
        await manager.ActivateAsync(sourceGenerationId, "synthetic-legacy-v1", CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
        return Task.CompletedTask;
    }

    private async Task<string> CreateAccount(string name, string type)
    {
        var suffix = (++sequence).ToString("D4", System.Globalization.CultureInfo.InvariantCulture)[^4..];
        return (await Success(
            ["ledger", "account", "create", "--input", "-"],
            Envelope(new JsonObject
            {
                ["institutionName"] = "Example Bank",
                ["displayName"] = name + " " + suffix,
                ["accountType"] = type,
                ["maskedIdentifier"] = "****" + suffix,
                ["currencyCode"] = "ZAR"
            }, Key("account")),
            "ledger.account.create")).GetProperty("accountId").GetString()!;
    }

    private async Task<JsonElement> Status() => await Success(
        ["ledger", "storage", "status", "--input", "-"],
        Envelope(new JsonObject()),
        "ledger.storage.status");

    private async Task<JsonElement> Prepare(string key) => AssertSuccess(
        await Run(["ledger", "storage", "evolution", "prepare", "--input", "-"], PrepareEnvelope(key, "uc012")),
        "ledger.storage.evolution.prepare");

    private Task<PublishedTallyResult> ActivateResult(JsonElement prepared, bool authorized, string key) => Run(
        ["ledger", "storage", "evolution", "activate", "--input", "-"],
        Envelope(ActivationInput(prepared, authorized), key));

    private async Task<JsonElement> Actuals() => await Success(
        ["ledger", "actuals", "query", "--input", "-"],
        Envelope(new JsonObject
        {
            ["filter"] = new JsonObject { ["groupBy"] = "pool_category" },
            ["pageSize"] = 100,
            ["cursor"] = null
        }),
        "ledger.actuals.query");

    private async Task<bool> KillPublishedProcessDuringMutation(IReadOnlyList<string> arguments, string input)
    {
        var start = new ProcessStartInfo(fixture.BinaryPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["TALLY_DATA_ROOT"] = dataRoot;
        using var process = Assert.IsType<System.Diagnostics.Process>(System.Diagnostics.Process.Start(start));
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        var killed = !process.HasExited;
        if (killed) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        return killed;
    }

    private Task<PublishedTallyResult> Run(IReadOnlyList<string> arguments, string? input = null) => fixture.RunAsync(dataRoot, arguments, input);

    private async Task<JsonElement> Success(IReadOnlyList<string> arguments, string input, string operationId) =>
        AssertSuccess(await Run(arguments, input), operationId);

    private async Task<string> CurrentId() => (await File.ReadAllTextAsync(Path.Combine(dataRoot, "CURRENT"))).Trim();

    private string Key(string purpose) => "uc012-" + purpose + "-" + Interlocked.Increment(ref sequence);

    private static string PrepareEnvelope(string key, string actorLabel) => Envelope(
        new JsonObject { ["targetSchemaVersion"] = CompleteLedgerSchema.CurrentVersion },
        key,
        actorLabel);

    private static JsonObject ActivationInput(JsonElement prepared, bool authorized) => new()
    {
        ["candidateId"] = prepared.GetProperty("candidateId").GetString(),
        ["expectedCurrentFingerprint"] = prepared.GetProperty("sourceFingerprint").GetString(),
        ["expectedCandidateFingerprint"] = prepared.GetProperty("candidateNormalizedFingerprint").GetString(),
        ["authorizeReplacement"] = authorized
    };

    private static string Envelope(JsonNode input, string? idempotencyKey = null, string actorLabel = "uc012")
    {
        var envelope = new JsonObject
        {
            ["contractVersion"] = "1.0",
            ["actor"] = new JsonObject { ["kind"] = "automation", ["label"] = actorLabel, ["runId"] = "published-e2e" },
            ["input"] = input
        };
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

    private static void AssertFingerprint(string? value)
    {
        Assert.NotNull(value);
        Assert.Equal(64, value.Length);
        Assert.All(value, character => Assert.True(char.IsAsciiHexDigit(character)));
    }

}
