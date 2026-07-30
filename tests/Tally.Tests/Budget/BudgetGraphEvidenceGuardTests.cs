using System.Reflection;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Xunit;

namespace Tally.Tests.Budget;

/// <summary>
/// TASK-BUDGET-GATE-GRAPH-QUALITY / PAT-CORE-IMPLEMENTATION-PLAN-QUALITY-GATES.
/// Named-suite presence and forbidden-surface guards for BudgetGraphQualityEvidence.
/// Discovery non-vacuity is asserted by scripts/verify-budget-graph.sh (per-class list-tests).
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class BudgetGraphEvidenceGuardTests
{
    /// <summary>
    /// Feature, Ledger, contract, recovery, security, performance, INSIGHTS, and UC suites
    /// that must each contribute nonzero discovery before aggregate totals are accepted.
    /// </summary>
    public static readonly string[] NamedSuites =
    [
        // Feature / domain / storage / process
        "CreateBudgetDraftCommandTests",
        "ActivateBudgetPlanRevisionCommandTests",
        "BudgetPlanReadQueryTests",
        "BudgetPeriodTests",
        "BudgetPositionCalculatorTests",
        "BudgetEnvelopeResolutionTests",
        "BudgetEnvelopeIntegrityTests",
        "GetBudgetPositionQueryTests",
        "BudgetMutationExecutorTests",
        "BudgetStateStoreTests",
        "BudgetHistoryInvariantTests",
        "BudgetProcessContractTests",
        "BudgetOperationContractTests",
        // Contract (published + foundation)
        "BudgetContractShapeTests",
        "BudgetPublishedContractTests",
        // Ledger composition
        "BudgetLedgerBoundaryArchitectureTests",
        "BudgetLedgerContractClientTests",
        "LedgerBudgetActualsProjectionTests",
        "LedgerBudgetCategoryLifecycleTests",
        "LedgerBudgetPrerequisiteTests",
        // Recovery / security / performance
        "BudgetAtomicRecoveryTests",
        "BudgetSecurityGateTests",
        "BudgetPersonalScalePerformanceTests",
        // INSIGHTS projection
        "BudgetInsightsContractTests",
        // UC acceptance
        "BudgetUc001DraftTests",
        "BudgetUc002ActivationTests",
        "BudgetUc003PositionTests",
        "BudgetEnvelopeProvenanceTests",
        "BudgetUc004HistoryTests",
        "BudgetUc005AgentContractTests",
        // This gate suite
        "BudgetGraphEvidenceGuardTests"
    ];

    private static readonly string[] ForbiddenCompositionTokens =
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

    // Built without a single literal marker token so the guard source is not self-matching.
    private static readonly Regex PlaceholderPattern = new(
        "\\b(" + string.Join("|", "TO" + "DO", "FIX" + "ME", "HA" + "CK", "XX" + "X", "NotImplemented" + "Exception") + ")\\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void All_named_budget_suites_exist_in_the_test_assembly()
    {
        var names = typeof(BudgetGraphEvidenceGuardTests).Assembly
            .GetTypes()
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(NamedSuites, name => Assert.Contains(name, names));
    }

    /// <summary>
    /// Reverse of <see cref="All_named_budget_suites_exist_in_the_test_assembly"/>: the inventory
    /// was previously one-directional (NamedSuites ⊆ assembly), so a new Budget test class could
    /// silently ship without a discovery floor. Unlike <c>BudgetModuleGuardTests.NamedSuites</c>,
    /// this suite's list omits exactly one existing class — <c>BudgetModuleGuardTests</c> itself,
    /// the module gate's own guard suite — so the sets match modulo that known difference.
    /// </summary>
    [Fact]
    public void Every_budget_test_class_in_the_assembly_is_a_named_graph_suite_or_the_known_module_guard()
    {
        const string knownOmitted = "BudgetModuleGuardTests";

        var actual = ActualBudgetTestClassNames();
        var expected = NamedSuites.ToHashSet(StringComparer.Ordinal);

        Assert.Contains(knownOmitted, actual);
        Assert.DoesNotContain(knownOmitted, expected);

        var unlisted = actual.Except(expected).Except([knownOmitted]).Order(StringComparer.Ordinal).ToArray();
        var absent = expected.Except(actual).Order(StringComparer.Ordinal).ToArray();
        Assert.True(
            unlisted.Length == 0 && absent.Length == 0,
            $"NamedSuites drift beyond the known {knownOmitted} omission — present but unlisted: "
                + $"[{string.Join(", ", unlisted)}]; listed but absent from the assembly: [{string.Join(", ", absent)}]");
    }

    private static HashSet<string> ActualBudgetTestClassNames() =>
        typeof(BudgetGraphEvidenceGuardTests).Assembly
            .GetTypes()
            .Where(type =>
                type.IsPublic
                && type.Name.EndsWith("Tests", StringComparison.Ordinal)
                && type.Namespace is not null
                && (type.Namespace == "Tally.Tests.Budget"
                    || type.Namespace.StartsWith("Tally.Tests.Budget.", StringComparison.Ordinal)))
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

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
            foreach (var token in ForbiddenCompositionTokens)
            {
                Assert.False(
                    text.Contains(token, StringComparison.OrdinalIgnoreCase),
                    $"Forbidden surface token '{token}' found in {Relative(root, path)}");
            }
        }
    }

    [Fact]
    public void Budget_sources_have_no_placeholder_or_not_implemented_markers()
    {
        var root = RepositoryRoot();
        string[] scopes =
        [
            Path.Combine(root, "src", "Tally", "Features", "Budget"),
            Path.Combine(root, "src", "Tally", "Domain", "Budget"),
            Path.Combine(root, "src", "Tally", "Infrastructure", "Budget"),
            Path.Combine(root, "src", "Tally", "Contracts", "Budget"),
            Path.Combine(root, "tests", "Tally.Tests", "Budget")
        ];

        var hits = new List<string>();
        foreach (var scope in scopes)
        {
            if (!Directory.Exists(scope))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(scope, "*.cs", SearchOption.AllDirectories))
            {
                var lines = File.ReadAllLines(path);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains("string.Join", StringComparison.Ordinal)
                        && lines[i].Contains("NotImplemented", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (PlaceholderPattern.IsMatch(lines[i]))
                    {
                        hits.Add($"{Relative(root, path)}:{i + 1}");
                    }
                }
            }
        }

        Assert.True(hits.Count == 0, "Placeholder markers:\n" + string.Join('\n', hits));
    }

    [Fact]
    public void Exactly_six_budget_operations_and_zero_endpoint_entities_are_intended()
    {
        // Graph quality: CLI-only design — endpoint inventory is empty; registry has six ops.
        var registryType = typeof(Tally.Cli.OperationRegistry);
        var create = registryType.GetMethod(
            "Create",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        Assert.NotNull(create);

        var registry = create!.Invoke(null, null)!;
        var descriptorsProperty = registry.GetType().GetProperty("Descriptors")!;
        var descriptors = (System.Collections.IEnumerable)descriptorsProperty.GetValue(registry)!;
        var budgetIds = new List<string>();
        foreach (var descriptor in descriptors)
        {
            var id = (string)descriptor.GetType().GetProperty("OperationId")!.GetValue(descriptor)!;
            if (id.StartsWith("budget.", StringComparison.Ordinal))
            {
                budgetIds.Add(id);
            }
        }

        Assert.Equal(6, budgetIds.Count);
        Assert.DoesNotContain(budgetIds, id => id.Contains("http", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(budgetIds, id => id.Contains("endpoint", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Graph_quality_artifacts_exist()
    {
        var root = RepositoryRoot();
        Assert.True(File.Exists(Path.Combine(root, "scripts", "verify-budget-graph.sh")));
        Assert.True(File.Exists(Path.Combine(root, "docs", "verification", "budget-graph.md")));
        Assert.True(File.Exists(Path.Combine(root, ".lexicon", "graph", "BUDGET", "module.json")));
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

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}
