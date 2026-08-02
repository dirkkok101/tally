using System.Reflection;
using System.Runtime.Versioning;
using Tally.Cli;
using Tally.Features.Classify.Contract;
using Xunit;

// Namespace is intentionally Tally.Tests (not Tally.Tests.Classify) so the graph-quality
// reverse inventory (ClassifyGraphEvidenceGuardTests) continues to omit this module-only
// suite — same separation as BudgetGraphEvidenceGuardTests vs BudgetModuleGuardTests.
namespace Tally.Tests;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-GATE-MODULE / bd-3l4k / VerifiedClassifyV1Module.
/// Final module-gate architecture and suite guards. Discovery non-vacuity and
/// multi-step convergence are asserted by scripts/verify-classify-module.sh.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassifyModuleGuardTests
{
    /// <summary>
    /// Named CLASSIFY suites the module gate requires with nonzero discovery,
    /// including graph evidence and this module guard.
    /// </summary>
    public static readonly string[] NamedSuites =
    [
        "ClassificationDeterminismPropertyTests",
        "ClassificationEngineTests",
        "ClassificationEvaluationInputCancellationTests",
        "ClassificationEvaluationInputLoaderTests",
        "EvaluateClassificationCommandTests",
        "EvaluationLimitTests",
        "EvaluationPersistenceTests",
        "OutcomeExplanationTests",
        "OutcomeInvalidationTests",
        "ClassificationRuleVocabularyTests",
        "NormalizerV1Tests",
        "RuleActivationTests",
        "RuleDraftPersistenceTests",
        "RuleRetirementTests",
        "SaveClassificationRuleTests",
        "ApplyAuthorizationTests",
        "ApplyPreviewTests",
        "ClassificationApplySagaTests",
        "ClassificationApplyCrashRecoveryTests",
        "ClassificationFeedbackTests",
        "FeedbackProposalTests",
        "AbandonCleanupTests",
        "ClassificationStatusTests",
        "StatusPrivacyTests",
        "ClassifyHistoryInvariantTests",
        "ClassifyStateStoreTests",
        "ClassifyOperationContractTests",
        "ClassifyPublishedContractTests",
        "ClassifyProcessContractTests",
        "ClassifyLedgerBoundaryArchitectureTests",
        "ClassifyLedgerContractClientTests",
        "LedgerClassificationMutationPreconditionTests",
        "LedgerClassificationProjectionTests",
        "LedgerClassifyPrerequisiteTests",
        "ClassifyArtifactProtectionTests",
        "ClassifySecurityGateTests",
        "OwnerRulebookGateTests",
        "ClassificationRuleValidationTests",
        "PrivateCorpusPrivacyTests",
        "PrivateCorpusReaderTests",
        "ValidationLimitTests",
        "ValidationPrivacyTests",
        "ClassifyUc001EvaluationTests",
        "ClassifyUc002OutcomeTests",
        "ClassifyUc003ApplyTests",
        "ClassifyUc004RulesTests",
        "ClassifyUc005FeedbackTests",
        "ClassifyUc006AgentContractTests",
        "ClassifyGraphEvidenceGuardTests",
        "ClassifyModuleGuardTests"
    ];

    private static readonly string[] RequiredGateScripts =
    [
        "verify-classify-module.sh",
        "verify-classify-graph.sh",
        "verify-classify-contract.sh",
        "verify-classify-security.sh",
        "verify-classify-owner-rulebook.sh"
    ];

    private static readonly string[] RequiredExternalDeps =
    [
        "EXT-CLASSIFY-AI-AGENT-HOST",
        "EXT-CLASSIFY-HOST-OS-SECURITY",
        "EXT-CLASSIFY-LEDGER-PUBLIC-CONTRACT",
        "EXT-CLASSIFY-PRIVATE-EVALUATION-CORPUS"
    ];

    [Fact]
    public void Exactly_twelve_classify_operations_are_published()
    {
        var classify = OperationRegistry.Create().Descriptors
            .Where(d => d.OperationId.StartsWith("classify.", StringComparison.Ordinal))
            .Select(d => d.OperationId)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ClassifyOperationIds.All.Order(StringComparer.Ordinal), classify);
        Assert.Equal(12, classify.Length);
        Assert.DoesNotContain(classify, id => id.Contains("http", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(classify, id => id.Contains("daemon", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(classify, id => id.Contains("sync", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(classify, id => id.Contains("watch", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(classify, id => id.Contains("endpoint", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void All_named_module_gate_suites_exist_in_the_test_assembly()
    {
        var names = typeof(ClassifyModuleGuardTests).Assembly
            .GetTypes()
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(NamedSuites, name => Assert.Contains(name, names));
    }

    /// <summary>
    /// Every public *Tests type under Tally.Tests.Classify* plus this module guard must appear
    /// in NamedSuites so a new suite cannot ship without a discovery floor.
    /// </summary>
    [Fact]
    public void Every_classify_and_module_guard_test_class_is_a_named_module_suite()
    {
        var actual = ActualModuleScopedTestClassNames();
        var expected = NamedSuites.ToHashSet(StringComparer.Ordinal);

        var unlisted = actual.Except(expected).Order(StringComparer.Ordinal).ToArray();
        var absent = expected.Except(actual).Order(StringComparer.Ordinal).ToArray();
        Assert.True(
            unlisted.Length == 0 && absent.Length == 0,
            $"NamedSuites drift — present but unlisted: [{string.Join(", ", unlisted)}]; "
                + $"listed but absent from the assembly: [{string.Join(", ", absent)}]");
    }

    private static HashSet<string> ActualModuleScopedTestClassNames()
    {
        var names = typeof(ClassifyModuleGuardTests).Assembly
            .GetTypes()
            .Where(type =>
                type.IsPublic
                && type.Name.EndsWith("Tests", StringComparison.Ordinal)
                && type.Namespace is not null
                && (type.Namespace == "Tally.Tests.Classify"
                    || type.Namespace.StartsWith("Tally.Tests.Classify.", StringComparison.Ordinal)
                    || type == typeof(ClassifyModuleGuardTests)))
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);
        return names;
    }

    [Fact]
    public void Named_suite_source_files_exist()
    {
        var root = RepositoryRoot();
        var classifyTests = Path.Combine(root, "tests", "Tally.Tests", "Classify");
        Assert.True(Directory.Exists(classifyTests));

        var sources = Directory.EnumerateFiles(classifyTests, "*Tests.cs", SearchOption.AllDirectories)
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

        Assert.True(File.Exists(Path.Combine(root, "docs", "verification", "classify-v1.md")));
        Assert.True(File.Exists(Path.Combine(root, "docs", "verification", "classify-graph.md")));
        Assert.True(File.Exists(Path.Combine(root, ".lexicon", "graph", "CLASSIFY", "module.json")));
    }

    [Fact]
    public void External_dependency_entities_exist_for_all_four_boundaries()
    {
        var root = RepositoryRoot();
        var dir = Path.Combine(root, ".lexicon", "graph", "CLASSIFY", "external-dependency");
        Assert.True(Directory.Exists(dir));

        foreach (var refCode in RequiredExternalDeps)
        {
            Assert.True(
                File.Exists(Path.Combine(dir, $"{refCode}.json")),
                $"Missing external dependency entity {refCode}");
        }
    }

    [Fact]
    public void Classify_composition_has_no_forbidden_http_ef_or_host_surfaces()
    {
        var root = RepositoryRoot();
        string[] scopes =
        [
            Path.Combine(root, "src", "Tally", "Features", "Classify"),
            Path.Combine(root, "src", "Tally", "Domain", "Classify"),
            Path.Combine(root, "src", "Tally", "Infrastructure", "Classify"),
            Path.Combine(root, "src", "Tally", "Contracts", "Classify"),
            Path.Combine(root, "src", "Tally", "Bootstrap", "Features", "ClassifyExtensions.cs"),
            Path.Combine(root, "src", "Tally", "Bootstrap", "Features", "ClassifyApplyExtensions.cs"),
            Path.Combine(root, "src", "Tally", "Bootstrap", "Features", "ClassifyCorpusExtensions.cs"),
            Path.Combine(root, "src", "Tally", "Bootstrap", "Features", "ClassifyEvaluationExtensions.cs"),
            Path.Combine(root, "src", "Tally", "Bootstrap", "Features", "ClassifyFeedbackExtensions.cs"),
            Path.Combine(root, "src", "Tally", "Bootstrap", "Features", "ClassifyValidationExtensions.cs")
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
        var dir = Path.Combine(root, ".lexicon", "graph", "CLASSIFY", "kill-criterion");
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

    [Fact]
    public void Graph_quality_evidence_artifacts_are_present_for_consumption()
    {
        // Module gate consumes ClassifyGraphQualityEvidence; require the evidence surface exists.
        var root = RepositoryRoot();
        Assert.True(File.Exists(Path.Combine(root, "scripts", "verify-classify-graph.sh")));
        Assert.True(File.Exists(Path.Combine(root, "docs", "verification", "classify-graph.md")));
        Assert.True(
            File.Exists(Path.Combine(root, "tests", "Tally.Tests", "Classify", "ClassifyGraphEvidenceGuardTests.cs")));
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
