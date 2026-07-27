using System.Reflection;
using System.Runtime.Versioning;
using Tally.Cli;
using Tally.Features.Budget.Contract;
using Xunit;

namespace Tally.Tests.Budget;

/// <summary>
/// TASK-BUDGET-GATE-MODULE / NFR-BUDGET-SELF-CONTAINED-LOCAL-OPERATION.
/// Final module-gate architecture and suite guards for VerifiedBudgetV1Module.
/// Discovery non-vacuity and multi-script convergence are asserted by
/// scripts/verify-budget-module.sh.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class BudgetModuleGuardTests
{
    /// <summary>
    /// Named suites that the module gate requires with nonzero discovery.
    /// Includes graph/evidence guard and this module guard.
    /// </summary>
    public static readonly string[] NamedSuites =
    [
        "CreateBudgetDraftCommandTests",
        "ActivateBudgetPlanRevisionCommandTests",
        "BudgetPlanReadQueryTests",
        "BudgetPeriodTests",
        "BudgetPositionCalculatorTests",
        "GetBudgetPositionQueryTests",
        "BudgetMutationExecutorTests",
        "BudgetStateStoreTests",
        "BudgetHistoryInvariantTests",
        "BudgetProcessContractTests",
        "BudgetOperationContractTests",
        "BudgetLedgerBoundaryArchitectureTests",
        "BudgetLedgerContractClientTests",
        "LedgerBudgetActualsProjectionTests",
        "LedgerBudgetCategoryLifecycleTests",
        "LedgerBudgetPrerequisiteTests",
        "BudgetPublishedContractTests",
        "BudgetAtomicRecoveryTests",
        "BudgetSecurityGateTests",
        "BudgetPersonalScalePerformanceTests",
        "BudgetInsightsContractTests",
        "BudgetUc001DraftTests",
        "BudgetUc002ActivationTests",
        "BudgetUc003PositionTests",
        "BudgetUc004HistoryTests",
        "BudgetUc005AgentContractTests",
        "BudgetGraphEvidenceGuardTests",
        "BudgetModuleGuardTests"
    ];

    private static readonly string[] RequiredGateScripts =
    [
        "verify-budget-module.sh",
        "verify-budget-graph.sh",
        "verify-budget-contract.sh",
        "verify-budget-recovery.sh",
        "verify-budget-security.sh",
        "verify-budget-performance.sh"
    ];

    private static readonly string[] RequiredExternalDeps =
    [
        "EXT-BUDGET-AI-AGENT-HOST",
        "EXT-BUDGET-HOST-OS-SECURITY",
        "EXT-BUDGET-INSIGHTS-CONSUMER-CONTRACT",
        "EXT-BUDGET-LEDGER-PUBLIC-CONTRACT"
    ];

    [Fact]
    public void Exactly_six_budget_operations_are_published()
    {
        var budget = OperationRegistry.Create().Descriptors
            .Where(d => d.OperationId.StartsWith("budget.", StringComparison.Ordinal))
            .Select(d => d.OperationId)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(BudgetOperationIds.All.Order(StringComparer.Ordinal), budget);
        Assert.Equal(6, budget.Length);
        Assert.DoesNotContain(budget, id => id.Contains("http", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(budget, id => id.Contains("daemon", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(budget, id => id.Contains("sync", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(budget, id => id.Contains("watch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void All_named_module_gate_suites_exist_in_the_test_assembly()
    {
        var names = typeof(BudgetModuleGuardTests).Assembly
            .GetTypes()
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(NamedSuites, name => Assert.Contains(name, names));
    }

    [Fact]
    public void Named_suite_source_files_exist_under_tests_Budget()
    {
        var root = RepositoryRoot();
        var budgetTests = Path.Combine(root, "tests", "Tally.Tests", "Budget");
        Assert.True(Directory.Exists(budgetTests));

        var sources = Directory.EnumerateFiles(budgetTests, "*Tests.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(NamedSuites, name => Assert.Contains(name, sources));
    }

    [Fact]
    public void Module_gate_scripts_and_reports_exist()
    {
        var root = RepositoryRoot();
        foreach (var script in RequiredGateScripts)
        {
            Assert.True(
                File.Exists(Path.Combine(root, "scripts", script)),
                $"Missing gate script scripts/{script}");
        }

        Assert.True(File.Exists(Path.Combine(root, "docs", "verification", "budget-v1.md")));
        Assert.True(File.Exists(Path.Combine(root, "docs", "verification", "budget-graph.md")));
        Assert.True(File.Exists(Path.Combine(root, ".lexicon", "graph", "BUDGET", "module.json")));
    }

    [Fact]
    public void External_dependency_entities_exist_for_all_four_boundaries()
    {
        var root = RepositoryRoot();
        var dir = Path.Combine(root, ".lexicon", "graph", "BUDGET", "external-dependency");
        Assert.True(Directory.Exists(dir));

        foreach (var refCode in RequiredExternalDeps)
        {
            Assert.True(
                File.Exists(Path.Combine(dir, $"{refCode}.json")),
                $"Missing external dependency entity {refCode}");
        }
    }

    [Fact]
    public void Budget_composition_has_no_forbidden_http_ef_or_host_surfaces()
    {
        var root = RepositoryRoot();
        string[] scopes =
        [
            Path.Combine(root, "src", "Tally", "Features", "Budget"),
            Path.Combine(root, "src", "Tally", "Domain", "Budget"),
            Path.Combine(root, "src", "Tally", "Infrastructure", "Budget"),
            Path.Combine(root, "src", "Tally", "Contracts", "Budget"),
            Path.Combine(root, "src", "Tally", "Bootstrap", "Features", "BudgetExtensions.cs"),
            Path.Combine(root, "src", "Tally", "Bootstrap", "Features", "BudgetStateExtensions.cs")
        ];

        string[] forbidden =
        [
            "FastEndpoints",
            "Aspire",
            "Npgsql",
            "EntityFramework",
            "Microsoft.AspNetCore",
            "HttpListener",
            "TcpListener",
            "WebApplication",
            "UseKestrel",
            "MapGet(",
            "MapPost(",
            "MapControllers",
            "AddPlugins",
            "Assembly.LoadFrom",
            "HttpClient",
            "DbContext",
            "IHostedService",
            "AddHostedService",
            "using MailKit",
            "using MimeKit",
            "WebSocket"
        ];

        var sources = scopes
            .SelectMany(path => Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)
                : File.Exists(path) ? [path] : Array.Empty<string>())
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(sources);

        foreach (var path in sources)
        {
            var text = File.ReadAllText(path);
            foreach (var token in forbidden)
            {
                Assert.False(
                    text.Contains(token, StringComparison.OrdinalIgnoreCase),
                    $"Forbidden surface token '{token}' found in {Path.GetRelativePath(root, path).Replace('\\', '/')}");
            }
        }
    }

    [Fact]
    public void Five_kill_criterion_entities_exist_and_are_monitored()
    {
        var root = RepositoryRoot();
        var dir = Path.Combine(root, ".lexicon", "graph", "BUDGET", "kill-criterion");
        Assert.True(Directory.Exists(dir));

        var files = Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(5, files.Length);

        foreach (var path in files)
        {
            var text = File.ReadAllText(path);
            Assert.Contains("\"monitored\": 1", text, StringComparison.Ordinal);
            Assert.Contains("evaluation_state", text, StringComparison.Ordinal);
        }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tally.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
