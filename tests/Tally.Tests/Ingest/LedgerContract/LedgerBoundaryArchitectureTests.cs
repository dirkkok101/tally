using System.Reflection;
using Tally.Cli;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Ingest.LedgerContract;

public sealed class LedgerBoundaryArchitectureTests
{
    // DD-INGEST-LEDGER-PUBLIC-INTEGRATION
    [Fact]
    public void Client_exposes_only_the_concrete_public_operation_seam()
    {
        var type = typeof(LedgerContractClient);
        var constructor = Assert.Single(type.GetConstructors());
        Assert.Equal([typeof(OperationRegistry), typeof(TallyProcess)], constructor.GetParameters().Select(parameter => parameter.ParameterType));

        var referencedTypes = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(member => member switch
            {
                MethodInfo method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType),
                ConstructorInfo item => item.GetParameters().Select(parameter => parameter.ParameterType),
                PropertyInfo property => [property.PropertyType],
                _ => []
            })
            .ToArray();

        Assert.DoesNotContain(referencedTypes, item => item.IsInterface && item.Name.Contains("LedgerTransport", StringComparison.Ordinal));
        Assert.DoesNotContain(referencedTypes, item => item.FullName?.StartsWith("Tally.Domain.Ledger", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(referencedTypes, item => item.FullName?.StartsWith("Tally.Infrastructure.Storage", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(referencedTypes, item => item.FullName?.StartsWith("Tally.Features.Ledger", StringComparison.Ordinal) == true);
    }

    // DD-INGEST-LEDGER-PUBLIC-INTEGRATION
    [Fact]
    public void Client_source_has_no_private_storage_handler_sql_or_child_process_path()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Tally", "Integration", "Ledger", "LedgerContractClient.cs"));
        string[] forbidden =
        [
            "LedgerDb", "Microsoft.Data.Sqlite", "Tally.Infrastructure.Storage", "Tally.Features.Ledger",
            "Tally.Domain.Ledger", "Process.Start", "System.Diagnostics", "ILedgerTransport", "connectionString", "dataRoot"
        ];

        Assert.All(forbidden, value => Assert.DoesNotContain(value, source, StringComparison.Ordinal));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tally.slnx"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
