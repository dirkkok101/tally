using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tally.Tests.Process;
using Xunit;

namespace Tally.Tests.EndToEnd;

[SupportedOSPlatform("linux")]
[Collection(PublishedTallyCollection.Name)]
public sealed class UC006ExternalOrchestratorContractWorkflowTests(PublishedTallyFixture fixture) : IAsyncLifetime
{
    private readonly string dataRoot = Path.Combine(Path.GetTempPath(), "tally-uc006-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task UC_LEDGER_006_version_help_and_contract_discovery_need_no_store_or_guidance()
    {
        var version = await SuccessWithoutStore(["version"], null, "system.version");
        var help = await SuccessWithoutStore(["help"], null, "system.schema.list");

        Assert.Equal("1.0", version.GetProperty("contractVersion").GetString());
        Assert.Equal("1.0", version.GetProperty("compatibility").GetString());
        Assert.Equal(73, help.GetProperty("operations").GetArrayLength());
        Assert.False(Directory.Exists(Path.Combine(dataRoot, ".agents")));
        Assert.False(Directory.Exists(Path.Combine(dataRoot, ".claude")));
    }

    [Fact]
    public async Task TC_LEDGER_AGENT_CONTRACT_CONFORMANCE_schema_inventory_is_byte_stable()
    {
        var first = await fixture.RunAsync(string.Empty, ["schema", "list"]);
        var second = await fixture.RunAsync(string.Empty, ["schema", "list"]);

        AssertSuccess(first, "system.schema.list");
        Assert.Equal(first.Stdout, second.Stdout);
    }

    [Fact]
    public async Task FR_LEDGER_CONTRACT_DISCOVERY_all_73_operations_have_complete_showable_schemas()
    {
        var list = await SuccessWithoutStore(["schema", "list"], null, "system.schema.list");
        var operations = list.GetProperty("operations").EnumerateArray().Select(operation => operation.Clone()).ToArray();
        var operationIds = operations.Select(operation => operation.GetProperty("operationId").GetString()!).ToArray();

        Assert.Equal(73, operations.Length);
        Assert.Equal(operationIds.Order(StringComparer.Ordinal), operationIds);
        foreach (var operation in operations)
        {
            Assert.Equal("1.0", operation.GetProperty("minimumContractVersion").GetString());
            Assert.Equal("1.0", operation.GetProperty("maximumContractVersion").GetString());
            Assert.False(string.IsNullOrWhiteSpace(operation.GetProperty("example").GetString()));
            Assert.Contains(operation.GetProperty("errors").EnumerateArray(), error =>
                error.GetProperty("code").GetString() == "validation.invalid_input"
                && error.GetProperty("exitCode").GetInt32() == 3);
            AssertSchemaObject(operation.GetProperty("requestSchema").GetString()!);
            AssertSchemaObject(operation.GetProperty("resultSchema").GetString()!);

            var shown = await SuccessWithoutStore(
                ["schema", "show", operation.GetProperty("operationId").GetString()!],
                null,
                "system.schema.show");
            Assert.Equal(operation.GetRawText(), shown.GetProperty("operation").GetRawText());
        }
    }

    [Fact]
    public async Task FR_LEDGER_CONTRACT_DISCOVERY_hierarchy_and_statement_correction_are_schema_constructible()
    {
        var reparent = (await SuccessWithoutStore(
            ["schema", "show", "ledger.category.reparent"],
            null,
            "system.schema.show")).GetProperty("operation");
        var reconciliation = (await SuccessWithoutStore(
            ["schema", "show", "ledger.reconciliation.apply"],
            null,
            "system.schema.show")).GetProperty("operation");
        using var reparentRequest = JsonDocument.Parse(reparent.GetProperty("requestSchema").GetString()!);
        using var reconciliationRequest = JsonDocument.Parse(reconciliation.GetProperty("requestSchema").GetString()!);

        Assert.Equal(
            new[] { "categoryId", "parentCategoryId", "reason" },
            reparentRequest.RootElement.GetProperty("properties").EnumerateObject().Select(property => property.Name));
        Assert.Contains(
            reconciliationRequest.RootElement.GetProperty("properties").GetProperty("disposition").GetProperty("enum").EnumerateArray(),
            value => value.GetString() == "correct_existing_from_statement");
    }

    [Fact]
    public async Task FR_LEDGER_STRUCTURED_INVOCATION_valid_input_writes_one_success_result_and_no_stderr()
    {
        var result = await Run(
            ["ledger", "account", "create", "--input", "-"],
            Envelope(AccountInput("Structured Success"), "structured-success"));

        var account = AssertSuccess(result, "ledger.account.create");
        Assert.Equal("Structured Success", account.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task FR_LEDGER_STRUCTURED_INVOCATION_domain_conflict_is_one_safe_stable_error()
    {
        await Success(
            ["ledger", "account", "create", "--input", "-"],
            Envelope(AccountInput("Domain Conflict"), "domain-conflict-first"),
            "ledger.account.create");

        var result = await Run(
            ["ledger", "account", "create", "--input", "-"],
            Envelope(AccountInput("Domain Conflict"), "domain-conflict-second"));

        AssertError(result, 5, "LEDGER-ACCOUNT-DUPLICATE");
    }

    [Fact]
    public async Task FR_LEDGER_STRUCTURED_INVOCATION_statement_correction_review_is_structured_and_safe()
    {
        var result = await Run(
            ["ledger", "reconciliation", "apply", "--input", "-"],
            StatementCorrectionEnvelope("statement-correction-review"));

        AssertError(result, 8, "operation.review_required");
    }

    [Fact]
    public async Task FR_LEDGER_SKILL_COMPATIBILITY_incompatible_contract_fails_before_mutation()
    {
        var input = AccountInput("Incompatible Contract");
        var result = await Run(
            ["ledger", "account", "create", "--input", "-"],
            Envelope(input, "incompatible-contract", "2.0"));

        AssertError(result, 3, "validation.invalid_input");
        Assert.DoesNotContain(await AccountNames(), name => name == "Incompatible Contract");
    }

    [Fact]
    public async Task FR_LEDGER_STRUCTURED_INVOCATION_malformed_json_fails_without_disclosure_or_mutation()
    {
        const string canary = "PRIVATE_MALFORMED_CANARY";
        var result = await Run(["ledger", "account", "create", "--input", "-"], "{\"secret\":\"" + canary + "\"");

        AssertError(result, 3, "validation.invalid_input");
        Assert.DoesNotContain(canary, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("agentmail poll")]
    [InlineData("mailbox acknowledge")]
    [InlineData("mime parse")]
    [InlineData("whatsapp send")]
    [InlineData("recipient allow")]
    [InlineData("schedule report")]
    [InlineData("delivery retry")]
    public async Task FR_LEDGER_CONTRACT_DISCOVERY_provider_or_transport_operations_are_unknown(string command)
    {
        var result = await Run(command.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        AssertError(result, 2, "operation.unknown");
    }

    [Theory]
    [InlineData("mailbox")]
    [InlineData("mime")]
    [InlineData("message")]
    [InlineData("recipient")]
    [InlineData("schedule")]
    [InlineData("delivery")]
    [InlineData("acknowledgement")]
    [InlineData("whatsApp")]
    [InlineData("agentMail")]
    [InlineData("rawEmailPayload")]
    [InlineData("rawStatementPayload")]
    [InlineData("databasePath")]
    [InlineData("sqlitePath")]
    public async Task FR_LEDGER_STRUCTURED_INVOCATION_forbidden_fields_are_rejected_before_mutation(string field)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var displayName = "Forbidden " + suffix;
        var input = AccountInput(displayName);
        input[field] = "PRIVATE_FIELD_CANARY_" + suffix;

        var result = await Run(
            ["ledger", "account", "create", "--input", "-"],
            Envelope(input, "forbidden-" + suffix));

        AssertError(result, 3, "validation.invalid_input");
        Assert.DoesNotContain(suffix, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(suffix, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(await AccountNames(), name => name == displayName);
    }

    [Fact]
    public async Task FR_LEDGER_IDEMPOTENT_WRITES_lost_output_is_recovered_and_changed_replay_conflicts()
    {
        var original = Envelope(AccountInput("Lost Output"), "lost-output");
        var first = await Run(["ledger", "account", "create", "--input", "-"], original);
        var replay = await Run(["ledger", "account", "create", "--input", "-"], original);
        var changed = await Run(
            ["ledger", "account", "create", "--input", "-"],
            Envelope(AccountInput("Changed Replay"), "lost-output"));

        AssertSuccess(first, "ledger.account.create");
        Assert.Equal(first.Stdout, replay.Stdout);
        AssertError(changed, 5, "LEDGER-IDEMPOTENCY-001");
        Assert.Single(await AccountNames(), name => name == "Lost Output");
        Assert.DoesNotContain(await AccountNames(), name => name == "Changed Replay");
    }

    [Fact]
    public async Task FR_LEDGER_IDEMPOTENT_WRITES_invalid_request_does_not_consume_the_identity()
    {
        var invalid = AccountInput("Corrected Request");
        invalid["rawPayload"] = "PRIVATE_RAW_PAYLOAD";
        AssertError(
            await Run(["ledger", "account", "create", "--input", "-"], Envelope(invalid, "corrected-request")),
            3,
            "validation.invalid_input");

        var corrected = await Run(
            ["ledger", "account", "create", "--input", "-"],
            Envelope(AccountInput("Corrected Request"), "corrected-request"));

        AssertSuccess(corrected, "ledger.account.create");
        Assert.Single(await AccountNames(), name => name == "Corrected Request");
    }

    [Fact]
    public async Task TC_LEDGER_SKILL_COMPATIBILITY_CONTRACT_missing_guidance_matches_registry_without_writing_scope()
    {
        var scope = Path.Combine(dataRoot, "missing-guidance");
        var schemas = await SuccessWithoutStore(["schema", "list"], null, "system.schema.list");
        var operationIds = schemas.GetProperty("operations").EnumerateArray()
            .Select(operation => operation.GetProperty("operationId").GetString()!).ToHashSet(StringComparer.Ordinal);
        var list = await Success(
            ["system", "guidance", "list", "--input", "-"],
            Envelope(new JsonObject { ["scopePath"] = scope }),
            "system.guidance.list");

        Assert.Equal(2, list.GetProperty("bundles").GetArrayLength());
        Assert.All(list.GetProperty("bundles").EnumerateArray(), bundle =>
        {
            Assert.Equal("missing", bundle.GetProperty("status").GetString());
            Assert.Equal("1.0", bundle.GetProperty("minimumContractVersion").GetString());
            Assert.Equal("1.0", bundle.GetProperty("maximumContractVersion").GetString());
            Assert.All(bundle.GetProperty("operationIds").EnumerateArray(), operationId =>
                Assert.Contains(operationId.GetString()!, operationIds));
        });
        Assert.False(Directory.Exists(scope));
    }

    [Fact]
    public async Task TC_LEDGER_SKILL_COMPATIBILITY_CONTRACT_optional_guidance_is_safe_versioned_and_compatible()
    {
        var scope = Path.Combine(dataRoot, "installed-guidance");
        var installed = await Success(
            ["system", "guidance", "install", "--input", "-"],
            Envelope(new JsonObject { ["host"] = "codex", ["scopePath"] = scope }, "install-guidance"),
            "system.guidance.install");
        var skillPath = installed.GetProperty("installPath").GetString()!;
        var content = await File.ReadAllTextAsync(skillPath);
        var manifest = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(skillPath)!, ".tally-guidance.json"));
        var check = await Success(
            ["system", "guidance", "check", "--input", "-"],
            Envelope(new JsonObject { ["host"] = "codex", ["scopePath"] = scope }),
            "system.guidance.check");

        Assert.Contains("tally schema list", content, StringComparison.Ordinal);
        Assert.Contains("tally schema show", content, StringComparison.Ordinal);
        Assert.Contains("\"minimumContractVersion\":\"1.0\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"maximumContractVersion\":\"1.0\"", manifest, StringComparison.Ordinal);
        Assert.Equal("compatible", check.GetProperty("bundle").GetProperty("status").GetString());
        foreach (var forbidden in new[] { "mailbox", "mime", "whatsapp", "delivery", "schedule", "recipient", "sqlite", "file access" })
        {
            Assert.DoesNotContain(forbidden, content, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task UC_LEDGER_006_removing_optional_guidance_leaves_hierarchy_and_statement_correction_invocable()
    {
        var scope = Path.Combine(dataRoot, "removed-guidance");
        await Success(
            ["system", "guidance", "install", "--input", "-"],
            Envelope(new JsonObject { ["host"] = "codex", ["scopePath"] = scope }, "install-removable-guidance"),
            "system.guidance.install");
        Directory.Delete(Path.Combine(scope, ".agents"), true);

        var schemas = await SuccessWithoutStore(["help"], null, "system.schema.list");
        Assert.Equal(73, schemas.GetProperty("operations").GetArrayLength());
        var firstParent = await CreateCategory("First parent", null, "first-parent");
        var secondParent = await CreateCategory("Second parent", null, "second-parent");
        var child = await CreateCategory("Child", firstParent.GetProperty("categoryId").GetString(), "child");
        var moved = await Success(
            ["ledger", "category", "reparent", "--input", "-"],
            Envelope(new JsonObject
            {
                ["categoryId"] = child.GetProperty("categoryId").GetString(),
                ["parentCategoryId"] = secondParent.GetProperty("categoryId").GetString(),
                ["reason"] = "Owner changed hierarchy"
            }, "reparent-without-guidance"),
            "ledger.category.reparent");

        Assert.Equal(secondParent.GetProperty("categoryId").GetString(), moved.GetProperty("category").GetProperty("parentCategoryId").GetString());
        AssertError(
            await Run(["ledger", "reconciliation", "apply", "--input", "-"], StatementCorrectionEnvelope("correction-without-guidance")),
            8,
            "operation.review_required");
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(dataRoot);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true);
        return Task.CompletedTask;
    }

    private async Task<JsonElement> CreateCategory(string name, string? parentCategoryId, string key) => await Success(
        ["ledger", "category", "create", "--input", "-"],
        Envelope(new JsonObject { ["name"] = name, ["parentCategoryId"] = parentCategoryId }, "category-" + key),
        "ledger.category.create");

    private async Task<IReadOnlyList<string>> AccountNames()
    {
        var result = await Success(
            ["ledger", "account", "list", "--input", "-"],
            Envelope(new JsonObject()),
            "ledger.account.list");
        return result.GetProperty("items").EnumerateArray().Select(account => account.GetProperty("displayName").GetString()!).ToArray();
    }

    private Task<PublishedTallyResult> Run(IReadOnlyList<string> arguments, string? input = null) =>
        fixture.RunAsync(dataRoot, arguments, input);

    private async Task<JsonElement> Success(IReadOnlyList<string> arguments, string? input, string operationId) =>
        AssertSuccess(await Run(arguments, input), operationId);

    private async Task<JsonElement> SuccessWithoutStore(IReadOnlyList<string> arguments, string? input, string operationId) =>
        AssertSuccess(await fixture.RunAsync(string.Empty, arguments, input), operationId);

    private static JsonObject AccountInput(string displayName) => new()
    {
        ["institutionName"] = "Example Bank",
        ["displayName"] = displayName,
        ["accountType"] = "cheque",
        ["maskedIdentifier"] = "****1234",
        ["currencyCode"] = "ZAR"
    };

    private static string StatementCorrectionEnvelope(string key) => Envelope(new JsonObject
    {
        ["evidenceId"] = "01J00000000000000000000000",
        ["evidenceFingerprint"] = new string('0', 64),
        ["scopeId"] = "01J00000000000000000000001",
        ["expectedProjectionToken"] = new string('1', 64),
        ["disposition"] = "correct_existing_from_statement",
        ["authorityKind"] = "deterministic_policy",
        ["reviewedCandidateIds"] = new JsonArray(),
        ["targetTransactionId"] = null,
        ["statementFact"] = null,
        ["exceptionCode"] = null,
        ["reason"] = "Statement correction requires owner review"
    }, key);

    private static string Envelope(JsonNode input, string? idempotencyKey = null, string contractVersion = "1.0")
    {
        var envelope = new JsonObject
        {
            ["contractVersion"] = contractVersion,
            ["actor"] = new JsonObject
            {
                ["kind"] = "automation",
                ["label"] = "uc006",
                ["runId"] = "published-e2e"
            },
            ["input"] = input
        };
        if (idempotencyKey is not null) envelope["idempotencyKey"] = idempotencyKey;
        return envelope.ToJsonString();
    }

    private static JsonElement AssertSuccess(PublishedTallyResult result, string operationId)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);
        Assert.Single(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("1.0", document.RootElement.GetProperty("contractVersion").GetString());
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
        Assert.Equal("system.process", document.RootElement.GetProperty("operationId").GetString());
        Assert.Equal("error", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(errorCode, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static void AssertSchemaObject(string schemaJson)
    {
        using var document = JsonDocument.Parse(schemaJson);
        Assert.Equal("object", document.RootElement.GetProperty("type").GetString());
        Assert.False(document.RootElement.GetProperty("additionalProperties").GetBoolean());
    }
}
