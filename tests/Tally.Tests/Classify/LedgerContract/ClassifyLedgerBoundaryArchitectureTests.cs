using System.Reflection;
using Tally.Cli;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Transactions;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Classify.LedgerContract;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-LEDGER-CLASSIFICATION-CLIENT / bd-2olb
/// Private-boundary and additive-client architecture guards for CLASSIFY Ledger composition.
/// </summary>
public sealed class ClassifyLedgerBoundaryArchitectureTests
{
    [Fact]
    public void Client_exposes_classify_methods_additively_on_the_concrete_public_seam()
    {
        var type = typeof(LedgerContractClient);
        var constructor = Assert.Single(type.GetConstructors());
        Assert.Equal(
            [typeof(OperationRegistry), typeof(TallyProcess)],
            constructor.GetParameters().Select(parameter => parameter.ParameterType));

        Assert.NotNull(type.GetMethod(nameof(LedgerContractClient.QueryClassificationProjectionAsync)));
        Assert.NotNull(type.GetMethod(nameof(LedgerContractClient.ListClassificationCategoriesAsync)));
        Assert.NotNull(type.GetMethod(nameof(LedgerContractClient.AssignCategoryAsync)));
        Assert.NotNull(type.GetMethod(nameof(LedgerContractClient.CorrectCategoryAsync)));

        // INGEST and BUDGET methods remain on the shared client (additive extension only).
        Assert.NotNull(type.GetMethod(nameof(LedgerContractClient.GetAccountAsync)));
        Assert.NotNull(type.GetMethod(nameof(LedgerContractClient.RecordTransactionAsync)));
        Assert.NotNull(type.GetMethod(nameof(LedgerContractClient.GetTransactionAsync)));
        Assert.NotNull(type.GetMethod(nameof(LedgerContractClient.ListBudgetCategoriesAsync)));
        Assert.NotNull(type.GetMethod(nameof(LedgerContractClient.QueryBudgetActualsAsync)));

        Assert.False(type.IsInterface);
        Assert.True(type.IsSealed);
        Assert.Empty(type.GetInterfaces());
    }

    [Fact]
    public void Classify_methods_surface_released_contract_types_and_cancellation()
    {
        var type = typeof(LedgerContractClient);

        var query = type.GetMethod(nameof(LedgerContractClient.QueryClassificationProjectionAsync))!;
        Assert.Equal(typeof(Task<LedgerContractResult<ActualsQueryResult>>), query.ReturnType);
        Assert.Contains(query.GetParameters(), p => p.ParameterType == typeof(ClassificationProjectionPurpose));
        Assert.Contains(query.GetParameters(), p => p.ParameterType == typeof(CancellationToken));

        var list = type.GetMethod(nameof(LedgerContractClient.ListClassificationCategoriesAsync))!;
        Assert.Equal(typeof(Task<LedgerContractResult<CategoryListResult>>), list.ReturnType);
        Assert.Contains(list.GetParameters(), p => p.ParameterType == typeof(CancellationToken));

        var assign = type.GetMethod(nameof(LedgerContractClient.AssignCategoryAsync))!;
        Assert.Equal(typeof(Task<LedgerContractResult<CategoryAllocationResult>>), assign.ReturnType);
        Assert.Contains(assign.GetParameters(), p => p.ParameterType == typeof(AssignCategoryInput));
        Assert.Contains(assign.GetParameters(), p => p.ParameterType == typeof(CancellationToken));

        var correct = type.GetMethod(nameof(LedgerContractClient.CorrectCategoryAsync))!;
        Assert.Equal(typeof(Task<LedgerContractResult<CategoryAllocationResult>>), correct.ReturnType);
        Assert.Contains(correct.GetParameters(), p => p.ParameterType == typeof(CorrectCategoryInput));
        Assert.Contains(correct.GetParameters(), p => p.ParameterType == typeof(CancellationToken));
    }

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

    [Fact]
    public void Client_source_has_no_private_storage_handler_sql_or_child_process_path()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Tally", "Integration", "Ledger", "LedgerContractClient.cs"));
        string[] forbidden =
        [
            "LedgerDb", "Microsoft.Data.Sqlite", "Tally.Infrastructure.Storage", "Tally.Features.Ledger",
            "Tally.Domain.Ledger", "Process.Start", "System.Diagnostics", "ILedgerTransport", "connectionString",
            "dataRoot", "HttpClient", "SqlConnection", "QuerySnapshotStore", "CategoryStore", "ActualsQueryHandler",
            "CategoryAllocationStore", "CategoryAllocationHandlers", "RelationshipStore"
        ];

        Assert.All(forbidden, value => Assert.DoesNotContain(value, source, StringComparison.Ordinal));
    }

    [Fact]
    public void Client_source_uses_only_public_executor_and_contracts()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Tally", "Integration", "Ledger", "LedgerContractClient.cs"));
        Assert.Contains("OperationRegistry", source, StringComparison.Ordinal);
        Assert.Contains("TallyProcess", source, StringComparison.Ordinal);
        Assert.Contains("process.RunAsync", source, StringComparison.Ordinal);
        Assert.Contains("QueryClassificationProjectionAsync", source, StringComparison.Ordinal);
        Assert.Contains("AssignCategoryAsync", source, StringComparison.Ordinal);
        Assert.Contains("CorrectCategoryAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessStartInfo", source, StringComparison.Ordinal);
        Assert.DoesNotContain("tally.dll", source, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<Type> Expand(Type type)
    {
        yield return type;
        if (!type.IsGenericType) yield break;
        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in Expand(argument))
            {
                yield return nested;
            }
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
