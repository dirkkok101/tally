using System.Reflection;
using System.Text.RegularExpressions;
using Tally.Cli;
using Tally.Contracts.Budget;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Domain.Budget.Periods;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Budget.LedgerContract;

/// <summary>
/// TASK-BUDGET-LEDGER-BUDGET-CLIENT / bd-2h45
/// Private-boundary and additive-client architecture guards for BUDGET Ledger composition.
/// </summary>
public sealed class BudgetLedgerBoundaryArchitectureTests
{
    // DD-BUDGET-LEDGER-PUBLIC-COMPOSITION / DD-BUDGET-APPLICATION-ARCHITECTURE
    [Fact]
    public void Client_exposes_budget_methods_additively_on_the_concrete_public_seam()
    {
        var type = typeof(LedgerContractClient);
        var constructor = Assert.Single(type.GetConstructors());
        Assert.Equal(
            [typeof(OperationRegistry), typeof(TallyProcess)],
            constructor.GetParameters().Select(parameter => parameter.ParameterType));

        Assert.NotNull(type.GetMethod(nameof(LedgerContractClient.ListBudgetCategoriesAsync)));
        Assert.NotNull(type.GetMethod(nameof(LedgerContractClient.GetBudgetCategoryAsync)));
        Assert.NotNull(type.GetMethod(nameof(LedgerContractClient.QueryBudgetActualsAsync)));

        // INGEST methods remain on the shared client (additive extension only).
        Assert.NotNull(type.GetMethod(nameof(LedgerContractClient.GetAccountAsync)));
        Assert.NotNull(type.GetMethod(nameof(LedgerContractClient.RecordTransactionAsync)));
        Assert.NotNull(type.GetMethod(nameof(LedgerContractClient.GetTransactionAsync)));

        // No transport interface, repository port, or speculative abstraction.
        Assert.False(type.IsInterface);
        Assert.True(type.IsSealed);
        Assert.Empty(type.GetInterfaces());
    }

    // DD-BUDGET-LEDGER-PUBLIC-COMPOSITION
    [Fact]
    public void Budget_methods_surface_released_contract_types_and_budget_period()
    {
        var type = typeof(LedgerContractClient);

        var list = type.GetMethod(nameof(LedgerContractClient.ListBudgetCategoriesAsync))!;
        Assert.Equal(typeof(Task<LedgerContractResult<CategoryListResult>>), list.ReturnType);
        Assert.Contains(list.GetParameters(), p => p.ParameterType == typeof(CategoryStatus?));

        var get = type.GetMethod(nameof(LedgerContractClient.GetBudgetCategoryAsync))!;
        Assert.Equal(typeof(Task<LedgerContractResult<CategoryDetail>>), get.ReturnType);

        var query = type.GetMethod(nameof(LedgerContractClient.QueryBudgetActualsAsync))!;
        Assert.Equal(typeof(Task<LedgerContractResult<ActualsQueryResult>>), query.ReturnType);
        Assert.Contains(query.GetParameters(), p => p.ParameterType == typeof(BudgetPeriod));
        Assert.Contains(query.GetParameters(), p => p.ParameterType == typeof(CancellationToken));
    }

    // DD-BUDGET-LEDGER-PUBLIC-COMPOSITION
    [Fact]
    public void Client_public_surface_references_no_private_ledger_or_transport_types()
    {
        var type = typeof(LedgerContractClient);
        var referencedTypes = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(member => member switch
            {
                MethodInfo method => method.GetParameters().Select(parameter => parameter.ParameterType)
                    .Append(method.ReturnType)
                    .Concat(method.ReturnType.IsGenericType ? method.ReturnType.GetGenericArguments() : []),
                ConstructorInfo item => item.GetParameters().Select(parameter => parameter.ParameterType),
                PropertyInfo property => new[] { property.PropertyType },
                _ => Array.Empty<Type>()
            })
            .SelectMany(Expand)
            .ToArray();

        Assert.DoesNotContain(referencedTypes, item => item.IsInterface && item.Name.Contains("LedgerTransport", StringComparison.Ordinal));
        Assert.DoesNotContain(referencedTypes, item => item.FullName?.StartsWith("Tally.Domain.Ledger", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(referencedTypes, item => item.FullName?.StartsWith("Tally.Infrastructure.Storage", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(referencedTypes, item => item.FullName?.StartsWith("Tally.Features.Ledger", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(referencedTypes, item => item.FullName?.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) == true);
    }

    // DD-BUDGET-LEDGER-PUBLIC-COMPOSITION
    [Fact]
    public void Client_source_has_no_private_storage_handler_sql_or_child_process_path()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Tally", "Integration", "Ledger", "LedgerContractClient.cs"));
        string[] forbidden =
        [
            "LedgerDb", "Microsoft.Data.Sqlite", "Tally.Infrastructure.Storage", "Tally.Features.Ledger",
            "Tally.Domain.Ledger", "Process.Start", "System.Diagnostics", "ILedgerTransport", "connectionString",
            "dataRoot", "HttpClient", "SqlConnection", "QuerySnapshotStore", "CategoryStore", "ActualsQueryHandler"
        ];

        Assert.All(forbidden, value => Assert.DoesNotContain(value, source, StringComparison.Ordinal));
    }

    // DD-BUDGET-LEDGER-PUBLIC-COMPOSITION
    [Fact]
    public void Client_source_uses_released_budget_operations_and_budget_compatibility_errors()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Tally", "Integration", "Ledger", "LedgerContractClient.cs"));

        Assert.Contains("ledger.category.list", source, StringComparison.Ordinal);
        Assert.Contains("ledger.category.get", source, StringComparison.Ordinal);
        Assert.Contains("ledger.actuals.query", source, StringComparison.Ordinal);
        Assert.Contains(nameof(BudgetErrors.LedgerIncompatible), source, StringComparison.Ordinal);
        Assert.Contains(nameof(BudgetErrors.Integrity), source, StringComparison.Ordinal);
        Assert.Contains(nameof(BudgetPeriod), source, StringComparison.Ordinal);
        Assert.Contains("ListBudgetCategoriesAsync", source, StringComparison.Ordinal);
        Assert.Contains("GetBudgetCategoryAsync", source, StringComparison.Ordinal);
        Assert.Contains("QueryBudgetActualsAsync", source, StringComparison.Ordinal);
        Assert.Contains("EndExclusive.AddDays(-1)", source, StringComparison.Ordinal);
        Assert.Contains("CancellationToken", source, StringComparison.Ordinal);
    }

    // Failure criterion: never catches unexpected exceptions into payloads
    [Fact]
    public void Client_source_does_not_catch_unexpected_exceptions_into_payloads()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Tally", "Integration", "Ledger", "LedgerContractClient.cs"));

        // No broad catch that maps unexpected exceptions into LedgerContractResult payloads.
        Assert.DoesNotContain("catch (Exception", source, StringComparison.Ordinal);
        Assert.DoesNotContain("catch\n", source, StringComparison.Ordinal);
        Assert.DoesNotContain("catch (", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"catch\s*\{", RegexOptions.CultureInvariant), source);
    }

    // DD-BUDGET-APPLICATION-ARCHITECTURE
    [Fact]
    public void Client_does_not_declare_an_extra_client_interface_or_repository_port()
    {
        var integrationRoot = Path.Combine(RepositoryRoot(), "src", "Tally", "Integration", "Ledger");
        foreach (var file in Directory.EnumerateFiles(integrationRoot, "*.cs"))
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("interface ILedger", source, StringComparison.Ordinal);
            Assert.DoesNotContain("interface IBudgetLedger", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ILedgerContractClient", source, StringComparison.Ordinal);
            Assert.DoesNotContain("IRepository", source, StringComparison.Ordinal);
        }

        Assert.Equal(BudgetErrors.LedgerIncompatible, "BUDGET-LEDGER-INCOMPATIBLE");
        Assert.Equal(BudgetErrors.Integrity, "BUDGET-INTEGRITY");
    }

    private static IEnumerable<Type> Expand(Type type)
    {
        yield return type;
        if (!type.IsGenericType) yield break;
        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in Expand(argument)) yield return nested;
        }
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
