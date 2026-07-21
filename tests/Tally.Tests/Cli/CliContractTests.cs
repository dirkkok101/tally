using System.Text.Json;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.System;
using Xunit;

namespace Tally.Tests.Cli;

public sealed class CliContractTests
{
    // TC-LEDGER-CONTRACT-DISCOVERY-CONTRACT
    [Fact] public void Registry_contains_exactly_73_provider_neutral_operations() => Assert.Equal(73, OperationRegistry.Create().Descriptors.Count);
    // TC-LEDGER-CONTRACT-DISCOVERY-CONTRACT
    [Fact]
    public void Schema_list_has_a_stable_independent_contract_inventory()
    {
        var first = OperationRegistry.Create();
        var second = OperationRegistry.Create();
        var operationIds = first.Descriptors.Select(x => x.OperationId).ToArray();

        Assert.Equal(operationIds.OrderBy(x => x, StringComparer.Ordinal), operationIds);
        Assert.Equal(73, operationIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("ledger.account.archive", operationIds[0]);
        Assert.Equal("system.version", operationIds[^1]);
        Assert.Equal(first.SchemaListJson(), second.SchemaListJson());
    }
    // TC-LEDGER-CONTRACT-DISCOVERY-CONTRACT
    [Fact]
    public void Schema_show_includes_complete_contract_metadata()
    {
        var schema = Assert.IsType<OperationSchema>(JsonSerializer.Deserialize(OperationRegistry.Create().SchemaShowJson("system.version"), LedgerJsonContext.Default.OperationSchema));
        Assert.Equal("query", schema.Kind);
        Assert.Equal("1.0", schema.MinimumContractVersion);
        Assert.Equal("1.0", schema.MaximumContractVersion);
        Assert.Equal(typeof(EmptyInput).FullName, schema.RequestType);
        Assert.Equal(typeof(VersionResult).FullName, schema.ResultType);
        Assert.Equal("SystemOperationModule.Version", schema.HandlerTarget);
        Assert.Contains(schema.Errors, error => error.ExitCode == 7 && error.Code == "contract.incompatible");
    }
    // TC-LEDGER-CONTRACT-DISCOVERY-CONTRACT
    [Fact]
    public async Task Version_is_a_structured_success()
    {
        var result = await Run("version");
        Assert.Equal(0, result.ExitCode);
        AssertEnvelope(result, "system.version", "success");
    }
    // TC-LEDGER-CONTRACT-DISCOVERY-CONTRACT
    [Fact]
    public async Task Help_is_descriptor_derived()
    {
        var result = await Run("help");
        Assert.Equal(0, result.ExitCode);
        AssertEnvelope(result, "system.schema.list", "success");
        Assert.Contains("ledger.account.create", result.Stdout);
    }
    // TC-LEDGER-STRUCTURED-INVOCATION-CONTRACT
    [Fact]
    public async Task Valid_json_writes_one_result_envelope()
    {
        var result = await Run(["schema", "show", "system.version", "--input", "-"], ValidEmptyRequest());
        Assert.Equal(0, result.ExitCode);
        Assert.Single(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        AssertEnvelope(result, "system.schema.show", "success");
    }
    // TC-LEDGER-STRUCTURED-INVOCATION-CONTRACT
    [Fact] public async Task Malformed_json_is_a_validation_error() => AssertError(await Run(["schema", "show", "system.version", "--input", "-"], "{"), 3, "validation.invalid_input");
    // TC-LEDGER-STRUCTURED-INVOCATION-CONTRACT
    [Fact] public async Task Unknown_fields_are_rejected_before_dispatch() => AssertError(await Run(["schema", "show", "system.version", "--input", "-"], "{\"mailbox\":\"x\"}"), 3, "validation.invalid_input");
    // TC-LEDGER-STRUCTURED-INVOCATION-CONTRACT
    [Fact] public async Task Provider_fields_are_rejected_before_dispatch() => AssertError(await Run(["schema", "show", "system.version", "--input", "-"], "{\"recipient\":\"x\"}"), 3, "validation.invalid_input");
    // TC-LEDGER-STRUCTURED-INVOCATION-CONTRACT
    [Fact] public async Task Provider_fields_inside_typed_input_are_rejected_before_dispatch() => AssertError(await Run(["version", "--input", "-"], "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"contract-test\"},\"input\":{\"recipient\":\"x\"}}"), 3, "validation.invalid_input");
    // TC-LEDGER-STRUCTURED-INVOCATION-CONTRACT
    [Fact]
    public async Task Safe_optional_run_id_is_accepted()
    {
        var request = "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"contract-test\",\"runId\":\"run-01\"},\"input\":{}}";
        AssertEnvelope(await Run(["version", "--input", "-"], request), "system.version", "success");
    }
    // TC-LEDGER-STRUCTURED-INVOCATION-CONTRACT
    [Fact]
    public async Task Unsafe_run_id_is_rejected_before_dispatch()
    {
        var request = "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"contract-test\",\"runId\":\"../private/path\"},\"input\":{}}";
        AssertError(await Run(["version", "--input", "-"], request), 3, "validation.invalid_input");
    }
    // TC-LEDGER-STRUCTURED-INVOCATION-CONTRACT
    [Fact] public async Task Missing_input_value_has_one_usage_envelope() => AssertError(await Run("version", "--input"), 2, "usage.invalid_input_path");
    // TC-LEDGER-STRUCTURED-INVOCATION-CONTRACT
    [Fact] public async Task Unsupported_input_path_has_one_usage_envelope() => AssertError(await Run("version", "--input", "/sensitive/input.json"), 2, "usage.invalid_input_path");
    // TC-LEDGER-STRUCTURED-INVOCATION-CONTRACT
    [Fact]
    public async Task File_input_accepts_valid_closed_envelope()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tally-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, ValidEmptyRequest());
        try { AssertEnvelope(await Run(["version", "--input", "@" + path]), "system.version", "success"); }
        finally { File.Delete(path); }
    }
    // TC-LEDGER-STRUCTURED-INVOCATION-CONTRACT
    [Fact]
    public async Task Malformed_file_input_is_validation_and_does_not_echo_path()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tally-secret-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "{");
        try
        {
            var result = await Run(["version", "--input", "@" + path]);
            AssertError(result, 3, "validation.invalid_input");
            Assert.DoesNotContain(path, result.Stderr, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }
    // TC-LEDGER-STRUCTURED-INVOCATION-CONTRACT
    [Fact]
    public async Task Missing_file_is_sanitized_unexpected_failure()
    {
        const string path = "/definitely-not-a-tally-input.json";
        var result = await Run(["version", "--input", "@" + path]);
        AssertError(result, 10, "host.unexpected");
        Assert.DoesNotContain(path, result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", result.Stderr, StringComparison.Ordinal);
    }
    // TC-LEDGER-STRUCTURED-INVOCATION-CONTRACT
    [Fact] public async Task Unknown_schema_show_operation_is_not_found() => AssertError(await Run("schema", "show", "ledger.provider.send"), 4, "operation.not_found");
    // TC-LEDGER-CONTRACT-DISCOVERY-CONTRACT
    [Fact]
    public void Read_operations_are_queries_and_never_require_idempotency()
    {
        var registry = OperationRegistry.Create();
        foreach (var operation in registry.Descriptors.Where(x => x.OperationId.Contains(".get", StringComparison.Ordinal) || x.OperationId.Contains(".list", StringComparison.Ordinal) || x.OperationId.Contains(".query", StringComparison.Ordinal) || x.OperationId.Contains(".candidates", StringComparison.Ordinal) || x.OperationId.Contains(".status", StringComparison.Ordinal) || x.OperationId.Contains(".verify", StringComparison.Ordinal)))
        {
            Assert.Equal("query", operation.Kind);
            Assert.False(operation.RequiresIdempotencyKey);
        }
    }
    // TC-LEDGER-STRUCTURED-INVOCATION-CONTRACT
    [Fact] public async Task Unknown_operation_has_stable_usage_exit() => AssertError(await Run("ledger", "invoke"), 2, "operation.unknown");

    private static async Task<ProcessResult> Run(params string[] args) => await Run(args, null);
    private static async Task<ProcessResult> Run(string[] args, string? input) => await new TallyProcess(OperationRegistry.Create()).RunAsync(args, input, CancellationToken.None);
    private static string ValidEmptyRequest() => "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"contract-test\"},\"input\":{}}";
    private static void AssertEnvelope(ProcessResult result, string operationId, string outcome)
    {
        Assert.Single(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        var envelope = Assert.IsType<ResultEnvelope>(JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope));
        Assert.Equal(operationId, envelope.OperationId);
        Assert.Equal(outcome, envelope.Outcome);
    }
    private static void AssertError(ProcessResult result, int exitCode, string code)
    {
        Assert.Equal(exitCode, result.ExitCode);
        AssertEnvelope(result, "system.process", "error");
        var envelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope);
        Assert.Equal(code, envelope!.Error!.Code);
    }
}
