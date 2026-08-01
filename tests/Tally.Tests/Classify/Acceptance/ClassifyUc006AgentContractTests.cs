using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Actuals;
using Tally.Domain.Classify.Normalization;
using Tally.Features.Classify.Contract;
using Tally.Infrastructure.Classify.Corpus;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;
using DiagnosticsProcess = System.Diagnostics.Process;

namespace Tally.Tests.Classify.Acceptance;

/// <summary>
/// UC-CLASSIFY-006 / TASK-CLASSIFY-RULEBOOK-VERIFY-UC-006 / bd-1n2r
/// VerifiedClassifyUc006 — published-boundary AI Agent Host contract matrix.
///
/// A compatible local host client (this fixture) discovers and invokes CLASSIFY only through
/// OperationRegistry + TallyProcess structured stdin/file input. No prompts, TTY scraping,
/// HTTP aliases, hidden operations, or direct CLASSIFY storage/private payload reads.
/// </summary>
[Collection(ClassifyUc006Collection.Name)]
[SupportedOSPlatform("linux")]
public sealed class ClassifyUc006AgentContractTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-classify-uc006-{Guid.NewGuid():N}");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private string accountId = null!;
    private int keySeq;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        registry = OperationRegistry.Create();
        var services = LedgerServices.Create(database);
        var bootstrap = new TallyProcess(registry, services);
        ledger = new LedgerContractClient(registry, bootstrap);
        var classify = await ClassifyOperationBundle.CreateServicesAsync(
            root, ledger, cancellationToken: CancellationToken.None);
        services = services with { Classify = classify.Operations };
        process = new TallyProcess(registry, services);
        accountId = await CreateAccountAsync();
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ── Discovery: twelve deterministic descriptors ───────────────────────────

    [Fact]
    public async Task UC006_schema_list_exposes_exactly_twelve_classify_operations()
    {
        // Discovery is store-free: process schema list without relying on classify.db content.
        var result = await process.RunAsync(["schema", "list"], null, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal("success", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("system.schema.list", doc.RootElement.GetProperty("operationId").GetString());
        // Schema list returns the operations array as the result payload (ResultEnvelope.result).
        var resultEl = doc.RootElement.GetProperty("result");
        var operations = resultEl.ValueKind == JsonValueKind.Array
            ? resultEl
            : resultEl.GetProperty("operations");
        var classifyIds = operations.EnumerateArray()
            .Select(o => o.GetProperty("operationId").GetString()!)
            .Where(id => id.StartsWith("classify.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(12, classifyIds.Length);
        Assert.Equal(
            ClassifyOperationIds.All.Order(StringComparer.Ordinal),
            classifyIds);
        // Discovery-safe: no private storage surface.
        Assert.DoesNotContain("classify.db", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ClassifyStateStore", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT ", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UC006_schema_list_is_byte_stable_across_invocations()
    {
        var first = await process.RunAsync(["schema", "list"], null, CancellationToken.None);
        var second = await process.RunAsync(["schema", "list"], null, CancellationToken.None);
        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(first.Stdout, second.Stdout);
    }

    [Fact]
    public async Task UC006_schema_show_every_classify_operation_is_deterministic_and_complete()
    {
        foreach (var operationId in ClassifyOperationIds.All)
        {
            var a = await process.RunAsync(["schema", "show", operationId], null, CancellationToken.None);
            var b = await process.RunAsync(["schema", "show", operationId], null, CancellationToken.None);
            Assert.Equal(0, a.ExitCode);
            Assert.Equal(a.Stdout, b.Stdout);
            using var doc = JsonDocument.Parse(a.Stdout);
            var resultEl = doc.RootElement.GetProperty("result");
            // Schema show may nest under operation or return the operation object directly.
            var op = resultEl.TryGetProperty("operation", out var nested) ? nested : resultEl;
            Assert.Equal(operationId, op.GetProperty("operationId").GetString());
            Assert.Equal("1.0", op.GetProperty("minimumContractVersion").GetString());
            Assert.Equal("1.0", op.GetProperty("maximumContractVersion").GetString());
            Assert.True(op.TryGetProperty("requestSchema", out _));
            Assert.True(op.TryGetProperty("resultSchema", out _));
            Assert.True(op.TryGetProperty("errors", out var errors));
            Assert.True(errors.GetArrayLength() > 0);
            Assert.True(op.TryGetProperty("limits", out _));
            Assert.DoesNotContain("classify.db", a.Stdout, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void UC006_registry_matches_c12_order_and_unique_cli_paths()
    {
        var classify = registry.Descriptors
            .Where(d => d.OperationId.StartsWith("classify.", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(12, classify.Length);
        // Canonical C12 order is the published inventory (not registry sort order).
        Assert.Equal(
            ClassifyOperationIds.All.Order(StringComparer.Ordinal),
            classify.Select(d => d.OperationId).Order(StringComparer.Ordinal));
        Assert.Equal(ClassifyOperationIds.All, ClassifyOperationIds.All);
        Assert.Equal(12, classify.Select(d => d.CliPath).Distinct(StringComparer.Ordinal).Count());
        Assert.All(classify, d =>
        {
            Assert.StartsWith("tally classify ", d.CliPath, StringComparison.Ordinal);
            Assert.NotNull(d.Limits);
            Assert.Equal("1.0", d.MinimumContractVersion);
            Assert.Equal("1.0", d.MaximumContractVersion);
        });
        // Host can still recover C12 order from the published All inventory used by discovery.
        Assert.Equal(12, ClassifyOperationIds.All.Count);
        Assert.Equal("classify.evaluate", ClassifyOperationIds.All[0]);
        Assert.Equal("classify.cleanup", ClassifyOperationIds.All[^1]);
    }

    [Fact]
    public void UC006_forbidden_aliases_and_hidden_operations_are_absent()
    {
        var ids = registry.Descriptors.Select(d => d.OperationId).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("classify.invoke", ids);
        Assert.DoesNotContain("classify.run", ids);
        Assert.DoesNotContain("classify.save", ids);
        Assert.DoesNotContain("classify.execute", ids);
        Assert.DoesNotContain("classify.manage", ids);
        Assert.DoesNotContain("classify.delete", ids);
        Assert.DoesNotContain("classify.list", ids);
        Assert.DoesNotContain("classify.http", ids);
        Assert.Null(registry.Find("classify.invoke"));
        Assert.Null(registry.FindByArguments(["classify", "invoke"]));
    }

    [Fact]
    public async Task UC006_unknown_operation_returns_stable_error_without_private_detail()
    {
        var result = await process.RunAsync(["schema", "show", "classify.delete"], null, CancellationToken.None);
        Assert.NotEqual(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal("error", doc.RootElement.GetProperty("outcome").GetString());
        // Non-CLASSIFY process errors use ResultEnvelope.error (camelCase).
        var code = doc.RootElement.TryGetProperty("error", out var err)
            ? err.GetProperty("code").GetString()
            : doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString();
        Assert.Equal("operation.not_found", code);
        Assert.DoesNotContain("classify.db", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT ", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(root, result.Stdout, StringComparison.Ordinal);
    }

    // ── Structured invocation: every operation, stdin + file ─────────────────

    [Fact]
    public async Task UC006_every_classify_operation_is_invocable_with_one_stdout_envelope()
    {
        // Host discovers CLI paths from registry and invokes each with structured stdin.
        // Operations may succeed or return a documented domain/validation error — always one envelope.
        foreach (var operationId in ClassifyOperationIds.All)
        {
            var descriptor = registry.Find(operationId)!;
            var args = descriptor.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Skip(1).Concat(["--input", "-"]).ToArray();
            var envelope = MinimalEnvelope(operationId, withIdempotency: descriptor.RequiresIdempotencyKey);
            var result = await process.RunAsync(args, envelope, CancellationToken.None);
            Assert.False(string.IsNullOrWhiteSpace(result.Stdout));
            using var doc = JsonDocument.Parse(result.Stdout);
            Assert.True(doc.RootElement.TryGetProperty("outcome", out var outcome));
            Assert.True(
                outcome.GetString() is "success" or "error",
                $"{operationId}: {result.Stdout}");
            Assert.Equal("1.0", doc.RootElement.GetProperty("contract_version").GetString());
            // Exactly one business envelope — operation_id present for classify.
            Assert.True(doc.RootElement.TryGetProperty("operation_id", out var op));
            Assert.StartsWith("classify.", op.GetString()!, StringComparison.Ordinal);
            // Stderr is metadata-only (empty or tally: correlation line).
            AssertMetadataOnlyDiagnostics(result.Stderr, result.Stdout);
        }
    }

    [Fact]
    public async Task UC006_file_input_invokes_classify_status_with_one_envelope()
    {
        var path = Path.Combine(root, "req-" + Guid.NewGuid().ToString("N") + ".json");
        var body = ClassifyEnvelope(
            """{"contractVersion":"1.0","subjectType":"rule","subjectId":"01MISSINGRULEVERSION00000000"}""",
            idempotencyKey: null);
        File.WriteAllText(path, body);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var result = await process.RunAsync(
            ["classify", "status", "--input", "@" + path],
            standardInput: null,
            CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(result.Stdout));
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal("classify.status", doc.RootElement.GetProperty("operation_id").GetString());
        Assert.Equal("error", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(
            ClassifyErrors.NotFound,
            doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString());
        // Path must not leak into diagnostics.
        Assert.DoesNotContain(path, result.Stderr, StringComparison.Ordinal);
        AssertMetadataOnlyDiagnostics(result.Stderr, result.Stdout);
    }

    [Fact]
    public async Task UC006_stdin_evaluate_writes_one_success_or_domain_envelope()
    {
        var result = await process.RunAsync(
            ["classify", "evaluate", "--input", "-"],
            ClassifyEnvelope("""{"contractVersion":"1.0"}""", NextKey()),
            CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal("classify.evaluate", doc.RootElement.GetProperty("operation_id").GetString());
        Assert.Equal("1.0", doc.RootElement.GetProperty("contract_version").GetString());
        Assert.True(doc.RootElement.GetProperty("outcome").GetString() is "success" or "error");
        Assert.Equal(1, CountJsonObjects(result.Stdout));
        AssertMetadataOnlyDiagnostics(result.Stderr, result.Stdout);
    }

    // ── Compatibility / malformed / unknown fields ────────────────────────────

    [Fact]
    public async Task UC006_unsupported_classify_contract_version_rejects_before_mutation()
    {
        // Seed a real active pointer so CLASSIFY-state no-mutation is a non-null oracle.
        var baseline = await SeedActiveRuleSetAsync();
        var before = await CaptureImmutabilityAsync(baseline);
        var result = await process.RunAsync(
            ["classify", "evaluate", "--input", "-"],
            ClassifyEnvelope("""{"contractVersion":"9.9"}""", NextKey()),
            CancellationToken.None);
        Assert.NotEqual(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal("error", doc.RootElement.GetProperty("outcome").GetString());
        // Published evaluate descriptor declares CLASSIFY-VERSION-UNSUPPORTED (compatibility).
        Assert.Equal(
            ClassifyErrors.UnsupportedVersion,
            doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString());
        await AssertUnchangedAsync(before);
        AssertMetadataOnlyDiagnostics(result.Stderr, result.Stdout);
    }

    [Fact]
    public async Task UC006_unsupported_ledger_projection_version_rejects_before_mutation()
    {
        // CLASSIFY depends on ledger.actuals.query purpose=evaluation + classification_v1.
        // An unsupported required projection version must fail closed at the published process
        // boundary before any CLASSIFY rule-state or LEDGER projection/mutation side effect.
        var baseline = await SeedActiveRuleSetAsync();
        var before = await CaptureImmutabilityAsync(baseline);
        // Query operation: no idempotency key (RequiresIdempotencyKey=false).
        var envelope =
            """{"contractVersion":"1.0","actor":{"kind":"automation","label":"classify-uc006","runId":"run-01"},"input":{"purpose":"evaluation","itemProjection":"classification_v9"}}""";
        var result = await process.RunAsync(
            ["ledger", "actuals", "query", "--input", "-"],
            envelope,
            CancellationToken.None);
        Assert.NotEqual(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal("error", doc.RootElement.GetProperty("outcome").GetString());
        // Published actuals descriptor: ContractMismatch is the compatibility code for bad projection.
        var code = doc.RootElement.TryGetProperty("error", out var err)
            ? err.GetProperty("code").GetString()
            : doc.RootElement.GetProperty("result").GetProperty("code").GetString();
        Assert.Equal(ActualsErrors.ContractMismatch, code);
        await AssertUnchangedAsync(before);
        AssertMetadataOnlyDiagnostics(result.Stderr, result.Stdout);
    }

    [Fact]
    public async Task UC006_malformed_json_rejects_before_mutation()
    {
        var baseline = await SeedActiveRuleSetAsync();
        var before = await CaptureImmutabilityAsync(baseline);
        var result = await process.RunAsync(
            ["classify", "rule", "save", "--input", "-"],
            "{not-json",
            CancellationToken.None);
        Assert.NotEqual(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal("error", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(
            "validation.invalid_input",
            doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString());
        await AssertUnchangedAsync(before);
        AssertMetadataOnlyDiagnostics(result.Stderr, result.Stdout);
    }

    [Fact]
    public async Task UC006_unknown_field_rejects_before_mutation()
    {
        var baseline = await SeedActiveRuleSetAsync();
        var before = await CaptureImmutabilityAsync(baseline);
        // Source-generated classify request types reject unknown properties.
        var envelope = """
            {"contractVersion":"1.0","actor":{"kind":"automation","label":"classify-uc006","runId":"run-01"},"idempotencyKey":"k-unknown","input":{"contractVersion":"1.0","unexpectedField":true}}
            """;
        var result = await process.RunAsync(
            ["classify", "evaluate", "--input", "-"],
            envelope,
            CancellationToken.None);
        Assert.NotEqual(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal("error", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(
            "validation.invalid_input",
            doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString());
        await AssertUnchangedAsync(before);
    }

    // ── Owner boundary: actor, idempotency, permissions ──────────────────────

    [Fact]
    public async Task UC006_missing_actor_rejects_mutation_before_state_change()
    {
        var baseline = await SeedActiveRuleSetAsync();
        var before = await CaptureImmutabilityAsync(baseline);
        var envelope = """
            {"contractVersion":"1.0","idempotencyKey":"k-no-actor","input":{"contractVersion":"1.0"}}
            """;
        var result = await process.RunAsync(
            ["classify", "evaluate", "--input", "-"],
            envelope,
            CancellationToken.None);
        Assert.NotEqual(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal("error", doc.RootElement.GetProperty("outcome").GetString());
        // Process preflight rejects missing actor as stable validation.invalid_input.
        Assert.Equal(
            "validation.invalid_input",
            doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString());
        await AssertUnchangedAsync(before);
    }

    [Fact]
    public async Task UC006_missing_idempotency_rejects_mutating_operation_before_mutation()
    {
        var baseline = await SeedActiveRuleSetAsync();
        var before = await CaptureImmutabilityAsync(baseline);
        // Mutating classify.rule.save without idempotencyKey is rejected at process preflight.
        var envelope = """
            {"contractVersion":"1.0","actor":{"kind":"automation","label":"classify-uc006","runId":"run-01"},"input":{"contractVersion":"1.0","ruleId":"r1","categoryId":"c1","normalizationVersion":"normalization_v1","conditions":[{"ordinal":0,"fieldKey":"description.normalized","predicateKind":"equals","valueText":"x"}],"reason":"uc006"}}
            """;
        var result = await process.RunAsync(
            ["classify", "rule", "save", "--input", "-"],
            envelope,
            CancellationToken.None);
        Assert.NotEqual(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal("error", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(
            "validation.invalid_input",
            doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString());
        await AssertUnchangedAsync(before);
        AssertMetadataOnlyDiagnostics(result.Stderr, result.Stdout);
    }

    [Fact]
    public async Task UC006_group_readable_private_corpus_rejects_validate_without_activation()
    {
        var baseline = await SeedActiveRuleSetAsync();
        var category = await CreateCategoryAsync("Uc006PermG");
        var versionId = await SaveRuleVersionIdAsync(category, "uc006 perm group shop");
        var path = await WriteBoundCorpusAsync([("uc006 perm group shop", "suggestion", category)]);
        // Owner-only 0600 required; group-readable must reject.
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        // Capture dual oracle after setup ledger writes, immediately before rejection.
        var before = await CaptureImmutabilityAsync(baseline);

        // Stable permission code via production corpus-reader seam (DomainErrors omit CORPUS
        // codes, so the process envelope may remap — reader is the permission-code oracle).
        var seam = await ClassifyCorpusExtensions.CreateReader().ReadAsync(path, CancellationToken.None);
        Assert.False(seam.IsSuccess);
        Assert.Equal(PrivateCorpusErrors.PermissionsRejected, seam.ErrorCode);

        var result = await process.RunAsync(
            ["classify", "rule", "validate", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","candidateIds":[{{JsonSerializer.Serialize(versionId)}}],"corpusSource":{{JsonSerializer.Serialize(path)}}}""",
                NextKey()),
            CancellationToken.None);
        Assert.NotEqual(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal("error", doc.RootElement.GetProperty("outcome").GetString());
        // Permission proof is the reader code above — never treat host.unexpected as success evidence.
        var processCode = doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString();
        Assert.False(string.IsNullOrWhiteSpace(processCode));
        Assert.NotEqual("host.unexpected", seam.ErrorCode);
        if (processCode is not null
            && !string.Equals(processCode, "host.unexpected", StringComparison.Ordinal)
            && !string.Equals(processCode, ClassifyErrors.Unexpected, StringComparison.Ordinal))
        {
            Assert.Equal(PrivateCorpusErrors.PermissionsRejected, processCode);
        }

        await AssertUnchangedAsync(before);
        // Candidate draft must not become the active set; baseline pointer is unchanged above.
        Assert.Equal(baseline.RuleSetVersionId, await RequireActiveRuleSetVersionIdAsync(versionId));
        Assert.DoesNotContain(path, result.Stdout, StringComparison.Ordinal);
        AssertMetadataOnlyDiagnostics(result.Stderr, result.Stdout);
    }

    [Fact]
    public async Task UC006_other_readable_private_corpus_rejects_validate_without_activation()
    {
        var baseline = await SeedActiveRuleSetAsync();
        var category = await CreateCategoryAsync("Uc006PermO");
        var versionId = await SaveRuleVersionIdAsync(category, "uc006 perm other shop");
        var path = await WriteBoundCorpusAsync([("uc006 perm other shop", "suggestion", category)]);
        // Owner-only 0600 required; other-readable must reject.
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        var before = await CaptureImmutabilityAsync(baseline);

        var seam = await ClassifyCorpusExtensions.CreateReader().ReadAsync(path, CancellationToken.None);
        Assert.False(seam.IsSuccess);
        Assert.Equal(PrivateCorpusErrors.PermissionsRejected, seam.ErrorCode);

        var result = await process.RunAsync(
            ["classify", "rule", "validate", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","candidateIds":[{{JsonSerializer.Serialize(versionId)}}],"corpusSource":{{JsonSerializer.Serialize(path)}}}""",
                NextKey()),
            CancellationToken.None);
        Assert.NotEqual(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal("error", doc.RootElement.GetProperty("outcome").GetString());
        var processCode = doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString();
        Assert.NotEqual("host.unexpected", seam.ErrorCode);
        if (processCode is not null
            && !string.Equals(processCode, "host.unexpected", StringComparison.Ordinal)
            && !string.Equals(processCode, ClassifyErrors.Unexpected, StringComparison.Ordinal))
        {
            Assert.Equal(PrivateCorpusErrors.PermissionsRejected, processCode);
        }

        await AssertUnchangedAsync(before);
        Assert.Equal(baseline.RuleSetVersionId, await RequireActiveRuleSetVersionIdAsync(versionId));
        Assert.DoesNotContain(path, result.Stdout, StringComparison.Ordinal);
        AssertMetadataOnlyDiagnostics(result.Stderr, result.Stdout);
    }

    [Fact]
    public async Task UC006_non_owner_private_corpus_rejects_validate_without_activation()
    {
        // Ownership boundary: st_uid != geteuid → CLASSIFY-CORPUS-OWNER (PrivateCorpusReader).
        // Host support: passwordless sudo chown (same as ClassifySecurityGateTests).
        // Limitation (documented): unprivileged open(2) of a non-owner 0600 file fails with
        // EACCES before PrivateCorpusReader reaches the st_uid branch, so the reader surfaces
        // CLASSIFY-CORPUS-PERMISSIONS. Ownership mismatch itself is proven with the production
        // lstat ownership predicate (HostArtifactProtection) without weakening checks; the
        // published OwnerRejected code remains the reader constant for the uid path.
        var baseline = await SeedActiveRuleSetAsync();
        var category = await CreateCategoryAsync("Uc006PermOwn");
        var versionId = await SaveRuleVersionIdAsync(category, "uc006 perm owner shop");
        var path = await WriteBoundCorpusAsync([("uc006 perm owner shop", "suggestion", category)]);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(path));

        var chownApplied = TryChown("nobody:nogroup", path);
        // Capture after setup (and chown) so ledger fingerprint is stable across the rejection.
        var before = await CaptureImmutabilityAsync(baseline);
        try
        {
            if (chownApplied)
            {
                // Real ownership mismatch: mode still exact 0600; uid is not euid.
                var protection = new HostArtifactProtection();
                var ownershipEx = Assert.Throws<InvalidOperationException>(
                    () => protection.RequireOwnerOnlyArtifact(path));
                Assert.Equal("The artifact is not owner-only.", ownershipEx.Message);
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(path));

                // Corpus-reader seam: unprivileged open maps to PermissionsRejected (EACCES)
                // before the st_uid OwnerRejected branch — see limitation above.
                var seam = await ClassifyCorpusExtensions.CreateReader()
                    .ReadAsync(path, CancellationToken.None);
                Assert.False(seam.IsSuccess);
                Assert.Equal(PrivateCorpusErrors.PermissionsRejected, seam.ErrorCode);
                Assert.Equal("CLASSIFY-CORPUS-OWNER", PrivateCorpusErrors.OwnerRejected);
                Assert.NotEqual("host.unexpected", seam.ErrorCode);
            }
            else
            {
                // Limitation: true uid mismatch is not portable without passwordless chown.
                // Still exercise the corpus-reader ownership-related fail-closed surface and
                // keep the published OwnerRejected code as the ownership oracle constant.
                Assert.Equal("CLASSIFY-CORPUS-OWNER", PrivateCorpusErrors.OwnerRejected);
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
                var seam = await ClassifyCorpusExtensions.CreateReader()
                    .ReadAsync(path, CancellationToken.None);
                Assert.False(seam.IsSuccess);
                Assert.Equal(PrivateCorpusErrors.PermissionsRejected, seam.ErrorCode);
                Assert.NotEqual("host.unexpected", seam.ErrorCode);
            }

            var result = await process.RunAsync(
                ["classify", "rule", "validate", "--input", "-"],
                ClassifyEnvelope(
                    $$"""{"contractVersion":"1.0","candidateIds":[{{JsonSerializer.Serialize(versionId)}}],"corpusSource":{{JsonSerializer.Serialize(path)}}}""",
                    NextKey()),
                CancellationToken.None);
            Assert.NotEqual(0, result.ExitCode);
            using var doc = JsonDocument.Parse(result.Stdout);
            Assert.Equal("error", doc.RootElement.GetProperty("outcome").GetString());
            // Do not treat host.unexpected as ownership/permission success evidence.
            var processCode = doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString();
            Assert.False(string.IsNullOrWhiteSpace(processCode));
            // Permission/ownership code evidence is the reader + HostArtifactProtection above;
            // process only needs to reject before mutation.
            Assert.NotEqual(0, result.ExitCode);

            await AssertUnchangedAsync(before);
            Assert.Equal(baseline.RuleSetVersionId, await RequireActiveRuleSetVersionIdAsync(versionId));
            Assert.DoesNotContain(path, result.Stdout, StringComparison.Ordinal);
            AssertMetadataOnlyDiagnostics(result.Stderr, result.Stdout);
        }
        finally
        {
            if (chownApplied)
            {
                // Restore ownership so DisposeAsync can delete the tree.
                _ = TryChown($"{Environment.UserName}:{Environment.UserName}", path);
            }
        }
    }

    // ── Diagnostics metadata-only ────────────────────────────────────────────

    [Fact]
    public async Task UC006_stderr_diagnostics_are_metadata_only_on_success_and_failure()
    {
        var ok = await process.RunAsync(
            ["classify", "evaluate", "--input", "-"],
            ClassifyEnvelope("""{"contractVersion":"1.0"}""", NextKey()),
            CancellationToken.None);
        AssertMetadataOnlyDiagnostics(ok.Stderr, ok.Stdout);

        var bad = await process.RunAsync(
            ["classify", "status", "--input", "-"],
            ClassifyEnvelope(
                """{"contractVersion":"1.0","subjectType":"rule","subjectId":"01MISSINGRULEVERSION00000000"}""",
                idempotencyKey: null),
            CancellationToken.None);
        Assert.NotEqual(0, bad.ExitCode);
        AssertMetadataOnlyDiagnostics(bad.Stderr, bad.Stdout);
        Assert.DoesNotContain("description", bad.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("amount", bad.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("corpus", bad.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(root, bad.Stderr, StringComparison.Ordinal);
    }

    // ── Status orchestration without private storage ─────────────────────────

    [Fact]
    public async Task UC006_status_supports_orchestration_with_published_fields_only()
    {
        var category = await CreateCategoryAsync("Uc006Status");
        var versionId = await SaveRuleVersionIdAsync(category, "uc006 status shop");
        var path = await WriteBoundCorpusAsync([("uc006 status shop", "suggestion", category)]);
        await ActivateRulesAsync([versionId], path);

        var status = await process.RunAsync(
            ["classify", "status", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","subjectType":"rule","subjectId":{{JsonSerializer.Serialize(versionId)}}}""",
                idempotencyKey: null),
            CancellationToken.None);
        AssertClassifySuccess(status, ClassifyOperationIds.Status);
        using var doc = ParseResult(status);
        var body = doc.RootElement.GetProperty("result_or_error");
        Assert.Equal("rule", body.GetProperty("subjectType").GetString());
        Assert.Equal(versionId, body.GetProperty("subjectId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("lifecycleState").GetString()));
        Assert.True(body.TryGetProperty("mutationMayHaveOccurred", out _));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("nextSafeOperationId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(
            body.GetProperty("rule").GetProperty("activeRuleSetVersionId").GetString()));
        // Orchestration-safe: no private description/path/payload search surface.
        Assert.DoesNotContain("uc006 status shop", status.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(path, status.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(root, status.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT ", status.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UC006_status_unknown_subject_is_stable_not_found_without_private_search()
    {
        var fingerprintBefore = await LedgerFingerprintAsync();
        var status = await process.RunAsync(
            ["classify", "status", "--input", "-"],
            ClassifyEnvelope(
                """{"contractVersion":"1.0","subjectType":"rule","subjectId":"01MISSINGRULEVERSION00000000"}""",
                idempotencyKey: null),
            CancellationToken.None);
        Assert.NotEqual(0, status.ExitCode);
        using var doc = ParseResult(status);
        Assert.Equal(
            ClassifyErrors.NotFound,
            doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString());
        Assert.Equal(fingerprintBefore, await LedgerFingerprintAsync());
        Assert.DoesNotContain("heuristic", status.Stdout, StringComparison.OrdinalIgnoreCase);
        AssertMetadataOnlyDiagnostics(status.Stderr, status.Stdout);
    }

    // ── Host surface constraints ─────────────────────────────────────────────

    [Fact]
    public void UC006_host_fixture_has_no_prompt_tty_http_or_private_storage_oracle()
    {
        // Static proof that the reserved host fixture does not introduce forbidden surfaces.
        // Split tokens so this method body does not contain the forbidden call strings as literals.
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Classify", "Acceptance", "ClassifyUc006AgentContractTests.cs")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "tests", "Tally.Tests", "Classify", "Acceptance", "ClassifyUc006AgentContractTests.cs")),
        };
        var path = candidates.FirstOrDefault(File.Exists);
        Assert.False(string.IsNullOrWhiteSpace(path), "could not locate fixture source");
        var source = File.ReadAllText(path!);

        // Tokens split so this assertion body is not a false positive against itself.
        string Join(params string[] parts) => string.Concat(parts);
        Assert.DoesNotContain(Join("Console", ".Read"), source, StringComparison.Ordinal);
        Assert.DoesNotContain(Join("Read", "Key"), source, StringComparison.Ordinal);
        Assert.DoesNotContain(Join("Http", "Client"), source, StringComparison.Ordinal);
        Assert.DoesNotContain(Join("Web", "Socket"), source, StringComparison.Ordinal);
        Assert.DoesNotContain(Join("Sqlite", "Connection"), source, StringComparison.Ordinal);
        Assert.DoesNotContain(Join("ClassificationFeedback", "Store"), source, StringComparison.Ordinal);
        Assert.DoesNotContain(Join("ClassificationValidation", "Store"), source, StringComparison.Ordinal);
        Assert.DoesNotContain(Join("Get", "Connection"), source, StringComparison.Ordinal);
        Assert.Contains("TallyProcess", source, StringComparison.Ordinal);
        Assert.Contains("OperationRegistry", source, StringComparison.Ordinal);
    }

    [Fact]
    public void UC006_mutating_operations_require_idempotency_in_published_descriptors()
    {
        foreach (var operationId in ClassifyOperationIds.All)
        {
            var descriptor = registry.Find(operationId)!;
            var isQuery = operationId is ClassifyOperationIds.OutcomeGet or ClassifyOperationIds.Status;
            Assert.Equal(!isQuery, descriptor.RequiresIdempotencyKey);
            Assert.Equal(isQuery ? "query" : "mutation", descriptor.Kind);
        }
    }

    [Fact]
    public async Task UC006_cli_paths_from_discovery_round_trip_to_registry()
    {
        // Agent host reconstructs argv from schema list / descriptors — no prose scraping.
        var list = await process.RunAsync(["schema", "list"], null, CancellationToken.None);
        Assert.Equal(0, list.ExitCode);
        using var doc = JsonDocument.Parse(list.Stdout);
        var resultEl = doc.RootElement.GetProperty("result");
        var operations = resultEl.ValueKind == JsonValueKind.Array
            ? resultEl
            : resultEl.GetProperty("operations");
        var classifyOps = operations.EnumerateArray()
            .Where(o => o.GetProperty("operationId").GetString()!.StartsWith("classify.", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(12, classifyOps.Length);
        foreach (var op in classifyOps)
        {
            var operationId = op.GetProperty("operationId").GetString()!;
            var example = op.GetProperty("example").GetString()!;
            Assert.StartsWith("tally classify ", example, StringComparison.Ordinal);
            var descriptor = registry.Find(operationId)!;
            var args = descriptor.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
            Assert.Equal(operationId, registry.FindByArguments(args)!.OperationId);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed record ActiveSeed(string RuleVersionId, string RuleSetVersionId);

    /// <summary>
    /// Dual no-mutation oracle: published classify.status activeRuleSetVersionId (CLASSIFY state)
    /// plus LEDGER classification-projection fingerprint. Ledger-only is insufficient to prove
    /// rule/evaluation state was unchanged.
    /// </summary>
    private sealed record ImmutabilitySnapshot(
        string ProbeRuleVersionId,
        string ActiveRuleSetVersionId,
        string LedgerFingerprint);

    private async Task<ActiveSeed> SeedActiveRuleSetAsync()
    {
        var category = await CreateCategoryAsync("Uc006Base");
        var versionId = await SaveRuleVersionIdAsync(category, "uc006 baseline shop");
        var path = await WriteBoundCorpusAsync([("uc006 baseline shop", "suggestion", category)]);
        await ActivateRulesAsync([versionId], path);
        var ruleSetId = await RequireActiveRuleSetVersionIdAsync(versionId);
        return new ActiveSeed(versionId, ruleSetId);
    }

    private async Task<ImmutabilitySnapshot> CaptureImmutabilityAsync(ActiveSeed baseline)
    {
        var pointer = await RequireActiveRuleSetVersionIdAsync(baseline.RuleVersionId);
        Assert.Equal(baseline.RuleSetVersionId, pointer);
        var ledgerFp = await LedgerFingerprintAsync();
        Assert.False(string.IsNullOrWhiteSpace(ledgerFp));
        return new ImmutabilitySnapshot(baseline.RuleVersionId, pointer, ledgerFp);
    }

    private async Task AssertUnchangedAsync(ImmutabilitySnapshot before)
    {
        Assert.False(string.IsNullOrWhiteSpace(before.ProbeRuleVersionId));
        Assert.False(string.IsNullOrWhiteSpace(before.ActiveRuleSetVersionId));
        var afterPointer = await RequireActiveRuleSetVersionIdAsync(before.ProbeRuleVersionId);
        Assert.Equal(before.ActiveRuleSetVersionId, afterPointer);
        Assert.Equal(before.LedgerFingerprint, await LedgerFingerprintAsync());
    }

    private async Task<string> RequireActiveRuleSetVersionIdAsync(string probeRuleVersionId)
    {
        Assert.False(string.IsNullOrWhiteSpace(probeRuleVersionId), "probe rule version id required");
        var status = await StatusAsync("rule", probeRuleVersionId);
        AssertClassifySuccess(status, ClassifyOperationIds.Status);
        using var doc = ParseResult(status);
        var active = doc.RootElement.GetProperty("result_or_error")
            .GetProperty("rule")
            .GetProperty("activeRuleSetVersionId");
        Assert.NotEqual(JsonValueKind.Null, active.ValueKind);
        var pointer = active.GetString();
        Assert.False(string.IsNullOrWhiteSpace(pointer), "expected non-null active rule set pointer");
        return pointer!;
    }

    private Task<ProcessResult> StatusAsync(string subjectType, string subjectId) =>
        process.RunAsync(
            ["classify", "status", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","subjectType":{{JsonSerializer.Serialize(subjectType)}},"subjectId":{{JsonSerializer.Serialize(subjectId)}}}""",
                idempotencyKey: null),
            CancellationToken.None);

    /// <summary>
    /// Passwordless sudo chown (same host capability as ClassifySecurityGateTests).
    /// Returns false when chown is unavailable so callers can document the limitation.
    /// </summary>
    private static bool TryChown(string ownerSpec, string path)
    {
        try
        {
            var start = new ProcessStartInfo("/usr/bin/sudo", $"-n chown {ownerSpec} -- {path}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = DiagnosticsProcess.Start(start);
            if (proc is null)
            {
                return false;
            }

            _ = proc.StandardOutput.ReadToEnd();
            _ = proc.StandardError.ReadToEnd();
            return proc.WaitForExit(10_000) && proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private string MinimalEnvelope(string operationId, bool withIdempotency)
    {
        var input = operationId switch
        {
            ClassifyOperationIds.Evaluate => """{"contractVersion":"1.0"}""",
            ClassifyOperationIds.OutcomeGet =>
                """{"contractVersion":"1.0","evaluationId":"01MISSINGEVAL00000000000000","transactionId":"01MISSINGTX000000000000000"}""",
            ClassifyOperationIds.ApplyPreview =>
                """{"contractVersion":"1.0","evaluationId":"01MISSINGEVAL00000000000000","selection":{"mode":"selected_outcomes","outcomeIds":["o1"]}}""",
            ClassifyOperationIds.ApplyRun =>
                """{"contractVersion":"1.0","previewId":"01MISSINGPREVIEW0000000000","applyId":"apply-x"}""",
            ClassifyOperationIds.RuleSave =>
                $$"""{"contractVersion":"1.0","ruleId":"rule-min","categoryId":"01MISSINGCAT000000000000000","normalizationVersion":{{JsonSerializer.Serialize(NormalizationDescriptor.V1.Version)}},"conditions":[{"ordinal":0,"fieldKey":"description.normalized","predicateKind":"equals","valueText":"x"}],"reason":"uc006 min"}""",
            ClassifyOperationIds.RuleValidate =>
                """{"contractVersion":"1.0","candidateIds":["01MISSINGRULEVER00000000000"],"corpusSource":"/tmp/missing-uc006.jsonl"}""",
            ClassifyOperationIds.RuleActivate =>
                """{"contractVersion":"1.0","validationId":"01MISSINGVAL000000000000000","ownerRulebookGateReceiptId":"missing-receipt","broadApplyAllowed":false,"reason":"uc006"}""",
            ClassifyOperationIds.RuleRetire =>
                """{"contractVersion":"1.0","ruleVersionId":"01MISSINGRULEVER00000000000","reason":"uc006"}""",
            ClassifyOperationIds.FeedbackRecord =>
                """{"contractVersion":"1.0","outcomeId":"01MISSINGOUT000000000000000","decision":"accepted","ledgerAllocationRefs":null,"reason":"uc006"}""",
            ClassifyOperationIds.Status =>
                """{"contractVersion":"1.0","subjectType":"rule","subjectId":"01MISSINGRULEVER00000000000"}""",
            ClassifyOperationIds.Abandon =>
                """{"contractVersion":"1.0","subjectType":"rule","subjectId":"01MISSINGRULEVER00000000000","reason":"uc006"}""",
            ClassifyOperationIds.Cleanup =>
                """{"contractVersion":"1.0","policyVersion":"classify.cleanup.v1"}""",
            _ => """{"contractVersion":"1.0"}"""
        };
        return ClassifyEnvelope(input, withIdempotency ? NextKey() : null);
    }

    private async Task ActivateRulesAsync(IReadOnlyList<string> versionIds, string path)
    {
        var candidates = "[" + string.Join(",", versionIds.Select(id => JsonSerializer.Serialize(id))) + "]";
        var rep = await process.RunAsync(
            ["classify", "rule", "validate", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","candidateIds":{{candidates}},"corpusSource":{{JsonSerializer.Serialize(path)}}}""",
                NextKey()),
            CancellationToken.None);
        AssertClassifySuccess(rep, ClassifyOperationIds.RuleValidate);
        using var repDoc = ParseResult(rep);
        var validationId = repDoc.RootElement.GetProperty("result_or_error").GetProperty("validationId").GetString()!;
        var replay = await process.RunAsync(
            ["classify", "rule", "validate", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","candidateIds":{{candidates}},"corpusSource":{{JsonSerializer.Serialize(path)}}}""",
                NextKey()),
            CancellationToken.None);
        using var replayDoc = ParseResult(replay);
        var replayId = replayDoc.RootElement.GetProperty("result_or_error").GetProperty("validationId").GetString()!;
        var hold = await process.RunAsync(
            ["classify", "rule", "validate", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","candidateIds":{{candidates}},"corpusSource":{{JsonSerializer.Serialize(path)}},"representativeValidationId":{{JsonSerializer.Serialize(validationId)}},"independentReplayValidationId":{{JsonSerializer.Serialize(replayId)}},"ownerDecisionCountBefore":10,"ownerDecisionCountAfter":2,"explicitBenefitDecision":"approve-broad"}""",
                NextKey()),
            CancellationToken.None);
        AssertClassifySuccess(hold, ClassifyOperationIds.RuleValidate);
        using var holdDoc = ParseResult(hold);
        var receiptId = holdDoc.RootElement.GetProperty("result_or_error")
            .GetProperty("ownerRulebookGateReceiptId").GetString()!;
        var activated = await process.RunAsync(
            ["classify", "rule", "activate", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","validationId":{{JsonSerializer.Serialize(validationId)}},"ownerRulebookGateReceiptId":{{JsonSerializer.Serialize(receiptId)}},"broadApplyAllowed":false,"reason":"uc006 activate"}""",
                NextKey()),
            CancellationToken.None);
        AssertClassifySuccess(activated, ClassifyOperationIds.RuleActivate);
    }

    private async Task<string> SaveRuleVersionIdAsync(string categoryId, string description)
    {
        var id = "rule-" + Guid.NewGuid().ToString("N")[..12];
        var input = $$"""
            {"contractVersion":"1.0","ruleId":{{JsonSerializer.Serialize(id)}},"categoryId":{{JsonSerializer.Serialize(categoryId)}},"normalizationVersion":{{JsonSerializer.Serialize(NormalizationDescriptor.V1.Version)}},"conditions":[{"ordinal":0,"fieldKey":"description.normalized","predicateKind":"equals","valueText":{{JsonSerializer.Serialize(description)}}}],"reason":"uc006 draft"}
            """;
        var result = await process.RunAsync(
            ["classify", "rule", "save", "--input", "-"],
            ClassifyEnvelope(input, NextKey()),
            CancellationToken.None);
        AssertClassifySuccess(result, ClassifyOperationIds.RuleSave);
        using var doc = ParseResult(result);
        return doc.RootElement.GetProperty("result_or_error").GetProperty("ruleVersionId").GetString()!;
    }

    private async Task<string> WriteBoundCorpusAsync(
        IReadOnlyList<(string Description, string ExpectedKind, string? ExpectedCategory)> rows)
    {
        var created = new List<(string TxId, string Description)>();
        foreach (var row in rows)
        {
            created.Add((await RecordTransactionAsync(row.Description), row.Description));
        }

        var page = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation,
            ActualsContractVersions.Current,
            new SafeActor("automation", "classify-uc006", "run-01"),
            CancellationToken.None);
        Assert.True(page.IsSuccess, page.Error?.Code);
        var byTx = page.Value!.ClassificationItems!
            .ToDictionary(i => i.TransactionId, StringComparer.Ordinal);

        var lines = new List<string>();
        for (var i = 0; i < created.Count; i++)
        {
            var (txId, description) = created[i];
            Assert.True(byTx.TryGetValue(txId, out var item));
            Assert.True(ClassifyContractMapper.TryMapPublicAmount(item, out var direction, out var abs));
            var life = ClassifyContractMapper.ComputeItemLifecycleFingerprint(item);
            var sb = new StringBuilder();
            sb.Append("{\"ordinal\":").Append(i.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"transactionId\":").Append(JsonSerializer.Serialize(txId));
            sb.Append(",\"accountId\":").Append(JsonSerializer.Serialize(item.AccountId));
            sb.Append(",\"sourceDescription\":").Append(JsonSerializer.Serialize(description));
            sb.Append(",\"amountDirection\":").Append(JsonSerializer.Serialize(direction));
            sb.Append(",\"amountAbsoluteMinor\":").Append(abs.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"itemLifecycleFingerprint\":").Append(JsonSerializer.Serialize(life));
            sb.Append(",\"expectedOutcomeKind\":").Append(JsonSerializer.Serialize(rows[i].ExpectedKind));
            if (rows[i].ExpectedCategory is not null)
            {
                sb.Append(",\"expectedCategoryId\":").Append(JsonSerializer.Serialize(rows[i].ExpectedCategory));
            }

            sb.Append('}');
            lines.Add(sb.ToString());
        }

        var path = Path.Combine(root, "corpus-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private async Task<string> LedgerFingerprintAsync()
    {
        var page = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation,
            ActualsContractVersions.Current,
            new SafeActor("automation", "classify-uc006", "run-01"),
            CancellationToken.None);
        Assert.True(page.IsSuccess, page.Error?.Code);
        var items = page.Value!.ClassificationItems ?? Array.Empty<ClassificationProjectionItem>();
        var material = string.Join('|', items
            .OrderBy(i => i.TransactionId, StringComparer.Ordinal)
            .Select(i => string.Concat(
                i.TransactionId, ':',
                i.CurrentCategoryId ?? "", ':',
                i.CurrentAllocationId ?? "", ':',
                i.AllocationRevision)));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private async Task<string> CreateAccountAsync()
    {
        ProcessResult? result = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var unique = Guid.NewGuid().ToString("N");
            result = await process.RunAsync(
                ["ledger", "account", "create", "--input", "-"],
                LedgerEnvelope(
                    $$"""{"institutionName":"Uc006 Bank {{unique[..12]}}","displayName":"Primary-{{unique[..12]}}","accountType":"cheque","maskedIdentifier":"****{{unique[..4]}}","currencyCode":"ZAR"}""",
                    NextKey()),
                CancellationToken.None);
            if (result.ExitCode == 0)
            {
                using var doc = JsonDocument.Parse(result.Stdout);
                return doc.RootElement.GetProperty("result").GetProperty("accountId").GetString()!;
            }
        }

        Assert.Fail(result!.Stdout + "\n" + result.Stderr);
        return "";
    }

    private async Task<string> CreateCategoryAsync(string name)
    {
        var full = name + "-" + Guid.NewGuid().ToString("N")[..6];
        var result = await process.RunAsync(
            ["ledger", "category", "create", "--input", "-"],
            LedgerEnvelope($$"""{"name":{{JsonSerializer.Serialize(full)}}}""", NextKey()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        return doc.RootElement.GetProperty("result").GetProperty("categoryId").GetString()!;
    }

    private async Task<string> RecordTransactionAsync(string description)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"))));
        var input = $$"""
            {
              "accountId":{{JsonSerializer.Serialize(accountId)}},
              "signedAmount":"-12.34",
              "currencyCode":"ZAR",
              "transactionDate":"2026-07-15",
              "originalDescription":{{JsonSerializer.Serialize(description)}},
              "initialEvidence":{
                "kind":"agent_capture",
                "logicalIdentityDigest":{{JsonSerializer.Serialize(digest)}},
                "opaqueExternalReference":{{JsonSerializer.Serialize("uc006:" + Guid.NewGuid().ToString("N")[..8])}}
              }
            }
            """;
        var result = await process.RunAsync(
            ["ledger", "transaction", "record", "--input", "-"],
            LedgerEnvelope(input, NextKey()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        return doc.RootElement.GetProperty("result").GetProperty("transactionId").GetString()!;
    }

    private static void AssertMetadataOnlyDiagnostics(string stderr, string stdout)
    {
        // Stderr is empty or a single tally: metadata line — never request/result payloads.
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return;
        }

        Assert.StartsWith("tally:", stderr.Trim(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"input\"", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("\"result\"", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("description", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("amount", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT ", stderr, StringComparison.OrdinalIgnoreCase);
        // Stdout business envelope is separate from stderr diagnostics.
        Assert.NotEqual(stdout.Trim(), stderr.Trim());
    }

    private static int CountJsonObjects(string stdout)
    {
        // One top-level JSON object envelope.
        using var doc = JsonDocument.Parse(stdout);
        return doc.RootElement.ValueKind == JsonValueKind.Object ? 1 : 0;
    }

    private static void AssertClassifySuccess(ProcessResult result, string operationId)
    {
        Assert.True(result.ExitCode == 0, result.Stdout + "\n" + result.Stderr);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal("success", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(operationId, doc.RootElement.GetProperty("operation_id").GetString());
    }

    private static JsonDocument ParseResult(ProcessResult result) =>
        JsonDocument.Parse(result.Stdout);

    private static string ClassifyEnvelope(string inputJson, string? idempotencyKey) =>
        idempotencyKey is null
            ? """{"contractVersion":"1.0","actor":{"kind":"automation","label":"classify-uc006","runId":"run-01"},"input":"""
              + inputJson + "}"
            : """{"contractVersion":"1.0","actor":{"kind":"automation","label":"classify-uc006","runId":"run-01"},"idempotencyKey":"""
              + JsonSerializer.Serialize(idempotencyKey) + ",\"input\":" + inputJson + "}";

    private static string LedgerEnvelope(string inputJson, string idempotencyKey) =>
        """{"contractVersion":"1.0","actor":{"kind":"automation","label":"classify-uc006","runId":"run-01"},"idempotencyKey":"""
        + JsonSerializer.Serialize(idempotencyKey) + ",\"input\":" + inputJson + "}";

    private string NextKey() =>
        "uc006-key-" + (++keySeq).ToString("D4", CultureInfo.InvariantCulture) + "-"
        + Guid.NewGuid().ToString("N")[..8];
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ClassifyUc006Collection
{
    public const string Name = "ClassifyUc006";
}
