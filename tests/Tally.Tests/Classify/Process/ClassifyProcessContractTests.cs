using System.Reflection;
using System.Text.Json;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Contracts.Classify;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Features.Classify.Contract;
using Xunit;

namespace Tally.Tests.Classify.Process;

/// <summary>
/// TC-CLASSIFY-STRUCTURED-INVOCATION-CONTRACT / bd-3g6y —
/// stdout/stderr/exit matrix, typed ClassifyResultEnvelope, correlation preservation.
/// </summary>
public sealed class ClassifyProcessContractTests
{
    private readonly OperationRegistry registry = OperationRegistry.Create();
    private readonly TallyProcess process;

    public ClassifyProcessContractTests()
    {
        // Descriptor-only services: no data root — validates envelope + stub preconditions.
        process = new TallyProcess(registry, LedgerServices.Create());
    }

    public static TheoryData<string, int, string> DeclaredClassifyErrors
    {
        get
        {
            var data = new TheoryData<string, int, string>();
            var declared = OperationRegistry.Create().Descriptors
                .Where(descriptor => descriptor.OperationId.StartsWith("classify.", StringComparison.Ordinal))
                .SelectMany(descriptor => descriptor.DomainErrors ?? [])
                .DistinctBy(schema => schema.Code, StringComparer.Ordinal);
            foreach (var schema in declared)
            {
                data.Add(schema.Code, schema.ExitCode, schema.Category);
            }

            return data;
        }
    }

    [Fact]
    public void Registry_declares_classify_domain_errors()
    {
        Assert.True(DeclaredClassifyErrors.Count() >= 10);
    }

    [Theory]
    [MemberData(nameof(DeclaredClassifyErrors))]
    public void Declared_classify_errors_map_to_public_process_contract(string code, int exitCode, string category)
    {
        var mapper = typeof(TallyProcess).GetMethod(
            "ErrorForHandler",
            BindingFlags.NonPublic | BindingFlags.Static);
        var result = Assert.IsType<ProcessResult>(mapper!.Invoke(null, [code, null]));

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal("tally: " + code, result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Equal(category, error.GetProperty("category").GetString());
    }

    [Fact]
    public async Task Schema_list_includes_exactly_twelve_classify_operations_with_limits()
    {
        var result = await process.RunAsync(["schema", "list"], null, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        // Non-CLASSIFY path uses ResultEnvelope.
        var operations = document.RootElement.GetProperty("result").GetProperty("operations")
            .EnumerateArray()
            .Where(e => e.GetProperty("operationId").GetString()!
                .StartsWith("classify.", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(12, operations.Length);
        Assert.All(operations, op =>
        {
            Assert.True(op.TryGetProperty("limits", out var limits));
            Assert.True(limits.TryGetProperty("max_memory_bytes", out _));
        });
    }

    [Fact]
    public async Task Unknown_classify_operation_fails_before_store()
    {
        var result = await process.RunAsync(
            ["classify", "invoke"],
            null,
            CancellationToken.None);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("operation.unknown", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("classify.db", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("classify.db", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mutating_classify_without_idempotency_fails_validation()
    {
        var body = JsonSerializer.Serialize(
            new RequestEnvelope(
                "1.0",
                new SafeActor("human", "owner"),
                JsonSerializer.SerializeToElement(
                    new ClassifyEvaluateRequest("1.0"),
                    ClassifyJsonContext.Default.ClassifyEvaluateRequest)),
            LedgerJsonContext.Default.RequestEnvelope);
        var result = await process.RunAsync(
            ["classify", "evaluate", "--input", "-"],
            body,
            CancellationToken.None);
        Assert.Equal(3, result.ExitCode);
        AssertClassifyEnvelope(result.Stdout, expectedOutcome: "error", ClassifyOperationIds.Evaluate);
    }

    [Fact]
    public async Task Classify_success_stub_emits_typed_envelope_with_correlation_ref()
    {
        // Outcome.get is a query: no idempotency; stub returns not-found after actor check,
        // but status/evaluate stubs still prove envelope shape. Use status with actor only.
        var body = JsonSerializer.Serialize(
            new RequestEnvelope(
                "1.0",
                new SafeActor("human", "owner", "run-corr"),
                JsonSerializer.SerializeToElement(
                    new ClassifyStatusRequest("1.0", ClassifyStatusSubjectType.Evaluation, "eval-missing"),
                    ClassifyJsonContext.Default.ClassifyStatusRequest),
                IdempotencyKey: null,
                CorrelationRef: "corr-status-1"),
            LedgerJsonContext.Default.RequestEnvelope);

        var result = await process.RunAsync(
            ["classify", "status", "--input", "-"],
            body,
            CancellationToken.None);

        // Stub without store returns NotFound after actor validation.
        Assert.NotEqual(0, result.ExitCode);
        using var document = AssertClassifyEnvelope(
            result.Stdout,
            expectedOutcome: "error",
            ClassifyOperationIds.Status);
        Assert.Equal("corr-status-1", document.RootElement.GetProperty("correlation_ref").GetString());
        Assert.Contains("correlation_ref=corr-status-1", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("eval-missing", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_classify_envelope_remains_result_envelope_bytes()
    {
        var result = await process.RunAsync(["version"], null, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("success", document.RootElement.GetProperty("outcome").GetString());
        Assert.True(document.RootElement.TryGetProperty("result", out _));
        Assert.False(document.RootElement.TryGetProperty("result_or_error", out _));
        Assert.False(document.RootElement.TryGetProperty("correlation_ref", out _));
        Assert.False(document.RootElement.TryGetProperty("contract_version", out _));
        Assert.True(document.RootElement.TryGetProperty("contractVersion", out _));
    }

    [Fact]
    public async Task Classify_error_uses_result_or_error_not_legacy_error_field()
    {
        var body = JsonSerializer.Serialize(
            new RequestEnvelope(
                "1.0",
                new SafeActor("human", "owner"),
                JsonSerializer.SerializeToElement(
                    new ClassifyStatusRequest("9.9", ClassifyStatusSubjectType.Evaluation, "x"),
                    ClassifyJsonContext.Default.ClassifyStatusRequest),
                CorrelationRef: "c-err"),
            LedgerJsonContext.Default.RequestEnvelope);
        var result = await process.RunAsync(
            ["classify", "status", "--input", "-"],
            body,
            CancellationToken.None);
        using var document = AssertClassifyEnvelope(
            result.Stdout, "error", ClassifyOperationIds.Status);
        Assert.False(document.RootElement.TryGetProperty("error", out _));
        Assert.False(document.RootElement.TryGetProperty("result", out _));
        var payload = document.RootElement.GetProperty("result_or_error");
        Assert.Equal(ClassifyErrors.UnsupportedVersion, payload.GetProperty("code").GetString());
    }

    [Fact]
    public void Classify_result_envelope_source_generated_metadata_exists()
    {
        Assert.NotNull(LedgerJsonContext.Default.ClassifyResultEnvelope);
        Assert.NotNull(LedgerJsonContext.Default.OperationLimits);
        var sample = new ClassifyResultEnvelope(
            "1.0",
            ClassifyOperationIds.Evaluate,
            "success",
            JsonSerializer.SerializeToElement(new { ok = true }),
            "corr-1");
        var json = JsonSerializer.Serialize(sample, LedgerJsonContext.Default.ClassifyResultEnvelope);
        Assert.Contains("\"contract_version\"", json, StringComparison.Ordinal);
        Assert.Contains("\"operation_id\"", json, StringComparison.Ordinal);
        Assert.Contains("\"result_or_error\"", json, StringComparison.Ordinal);
        Assert.Contains("\"correlation_ref\"", json, StringComparison.Ordinal);
    }

    private static JsonDocument AssertClassifyEnvelope(
        string stdout,
        string expectedOutcome,
        string expectedOperationId)
    {
        var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal("1.0", root.GetProperty("contract_version").GetString());
        Assert.Equal(expectedOperationId, root.GetProperty("operation_id").GetString());
        Assert.Equal(expectedOutcome, root.GetProperty("outcome").GetString());
        Assert.True(root.TryGetProperty("result_or_error", out _));
        return document;
    }
}
