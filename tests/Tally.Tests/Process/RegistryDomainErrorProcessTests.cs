using System.Reflection;
using System.Text.Json;
using Tally.Cli;
using Tally.Contracts.Common;
using Xunit;

namespace Tally.Tests.Process;

/// <summary>
/// Registry-wide generalization of the per-module error-mapping theories: for EVERY descriptor
/// that declares DomainErrors, TallyProcess.ErrorForHandler must map each declared code through
/// the descriptor's own ErrorSchema to exactly the declared exit code and category
/// (DD-LEDGER/INGEST/BUDGET-CLI-OPERATION-CONTRACT: the registry generates the stable errors).
/// </summary>
public sealed class RegistryDomainErrorProcessTests
{
    [Fact]
    public void Registry_declares_domain_errors_across_modules()
    {
        // Guard the guard: an empty enumeration would turn the theories below into a no-op.
        Assert.True(DeclaredDomainErrors.Count() >= 100);
    }

    [Theory]
    [MemberData(nameof(DeclaredDomainErrors))]
    public void Declared_domain_errors_map_from_their_descriptor_schema(string operationId, string code, int exitCode, string category)
    {
        var descriptor = OperationRegistry.Create().Find(operationId);
        Assert.NotNull(descriptor);

        var result = Assert.IsType<ProcessResult>(Mapper().Invoke(null, [code, descriptor]));

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal("tally: " + code, result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Equal(category, error.GetProperty("category").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
    }

    /// <summary>
    /// The retained fallback switch handles codes emitted while a DIFFERENT descriptor is
    /// invoked (cross-descriptor emission). For every declared code it must agree with the
    /// declaration, or the same failure would map differently depending on the entry operation.
    /// </summary>
    [Theory]
    [MemberData(nameof(DeclaredDomainErrors))]
    public void Declared_domain_errors_keep_the_fallback_switch_coherent(string operationId, string code, int exitCode, string category)
    {
        Assert.NotNull(operationId);

        var result = Assert.IsType<ProcessResult>(Mapper().Invoke(null, [code, null]));

        Assert.Equal(exitCode, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(category, document.RootElement.GetProperty("error").GetProperty("category").GetString());
    }

    public static TheoryData<string, string, int, string> DeclaredDomainErrors
    {
        get
        {
            var data = new TheoryData<string, string, int, string>();
            foreach (var descriptor in OperationRegistry.Create().Descriptors)
            {
                foreach (var schema in descriptor.DomainErrors ?? [])
                {
                    data.Add(descriptor.OperationId, schema.Code, schema.ExitCode, schema.Category);
                }
            }

            return data;
        }
    }

    private static MethodInfo Mapper() =>
        typeof(TallyProcess).GetMethod("ErrorForHandler", BindingFlags.NonPublic | BindingFlags.Static)!;
}
