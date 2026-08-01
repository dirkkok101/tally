using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Xunit;

namespace Tally.Tests.Classify;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-GATE-GRAPH-QUALITY / PAT-CORE-IMPLEMENTATION-PLAN-QUALITY-GATES / bd-1yaj.
/// Named-suite presence and forbidden-surface guards for ClassifyGraphQualityEvidence.
/// Per-class discovery non-vacuity is asserted by scripts/verify-classify-graph.sh.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassifyGraphEvidenceGuardTests
{
    /// <summary>
    /// Feature, integration/ledger, security, private-evidence, contract, recovery, storage,
    /// and UC suites that must each contribute nonzero discovery before aggregate totals.
    /// </summary>
    public static readonly string[] NamedSuites =
    [
        // Feature — evaluation / engine
        "ClassificationDeterminismPropertyTests",
        "ClassificationEngineTests",
        "ClassificationEvaluationInputCancellationTests",
        "ClassificationEvaluationInputLoaderTests",
        "EvaluateClassificationCommandTests",
        "EvaluationLimitTests",
        "EvaluationPersistenceTests",
        "OutcomeExplanationTests",
        "OutcomeInvalidationTests",
        // Feature — rules
        "ClassificationRuleVocabularyTests",
        "NormalizerV1Tests",
        "RuleActivationTests",
        "RuleDraftPersistenceTests",
        "RuleRetirementTests",
        "SaveClassificationRuleTests",
        // Feature — apply / recovery apply
        "ApplyAuthorizationTests",
        "ApplyPreviewTests",
        "ClassificationApplySagaTests",
        "ClassificationApplyCrashRecoveryTests",
        // Feature — feedback / recovery status
        "ClassificationFeedbackTests",
        "FeedbackProposalTests",
        "AbandonCleanupTests",
        "ClassificationStatusTests",
        "StatusPrivacyTests",
        // Storage
        "ClassifyHistoryInvariantTests",
        "ClassifyStateStoreTests",
        // Contract / process
        "ClassifyOperationContractTests",
        "ClassifyPublishedContractTests",
        "ClassifyProcessContractTests",
        // Integration — LEDGER public contract
        "ClassifyLedgerBoundaryArchitectureTests",
        "ClassifyLedgerContractClientTests",
        "LedgerClassificationMutationPreconditionTests",
        "LedgerClassificationProjectionTests",
        "LedgerClassifyPrerequisiteTests",
        // Security
        "ClassifyArtifactProtectionTests",
        "ClassifySecurityGateTests",
        // Private-evidence / validation
        "OwnerRulebookGateTests",
        "ClassificationRuleValidationTests",
        "PrivateCorpusPrivacyTests",
        "PrivateCorpusReaderTests",
        "ValidationLimitTests",
        "ValidationPrivacyTests",
        // UC acceptance
        "ClassifyUc001EvaluationTests",
        "ClassifyUc002OutcomeTests",
        "ClassifyUc003ApplyTests",
        "ClassifyUc004RulesTests",
        "ClassifyUc005FeedbackTests",
        "ClassifyUc006AgentContractTests",
        // This gate suite
        "ClassifyGraphEvidenceGuardTests"
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
    public void All_named_classify_suites_exist_in_the_test_assembly()
    {
        var names = typeof(ClassifyGraphEvidenceGuardTests).Assembly
            .GetTypes()
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(NamedSuites, name => Assert.Contains(name, names));
    }

    /// <summary>
    /// Reverse inventory: every public Classify *Tests class must appear in NamedSuites so a new
    /// suite cannot ship without a discovery floor. Module-final gate suites are not present yet
    /// (bd-3l4k owns TASK-CLASSIFY-RULEBOOK-GATE-MODULE).
    /// </summary>
    [Fact]
    public void Every_classify_test_class_in_the_assembly_is_a_named_graph_suite()
    {
        var actual = ActualClassifyTestClassNames();
        var expected = NamedSuites.ToHashSet(StringComparer.Ordinal);

        var unlisted = actual.Except(expected).Order(StringComparer.Ordinal).ToArray();
        var absent = expected.Except(actual).Order(StringComparer.Ordinal).ToArray();
        Assert.True(
            unlisted.Length == 0 && absent.Length == 0,
            "NamedSuites drift — present but unlisted: "
                + $"[{string.Join(", ", unlisted)}]; listed but absent from the assembly: [{string.Join(", ", absent)}]");
    }

    private static HashSet<string> ActualClassifyTestClassNames() =>
        typeof(ClassifyGraphEvidenceGuardTests).Assembly
            .GetTypes()
            .Where(type =>
                type.IsPublic
                && type.Name.EndsWith("Tests", StringComparison.Ordinal)
                && type.Namespace is not null
                && (type.Namespace == "Tally.Tests.Classify"
                    || type.Namespace.StartsWith("Tally.Tests.Classify.", StringComparison.Ordinal)))
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Named_suite_source_files_exist_under_tests_Classify()
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
    public void Classify_sources_have_no_placeholder_or_not_implemented_markers()
    {
        var root = RepositoryRoot();
        string[] scopes =
        [
            Path.Combine(root, "src", "Tally", "Features", "Classify"),
            Path.Combine(root, "src", "Tally", "Domain", "Classify"),
            Path.Combine(root, "src", "Tally", "Infrastructure", "Classify"),
            Path.Combine(root, "src", "Tally", "Contracts", "Classify"),
            Path.Combine(root, "tests", "Tally.Tests", "Classify")
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
    public void Exactly_twelve_classify_operations_and_zero_http_aliases_are_intended()
    {
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
        var classifyIds = new List<string>();
        foreach (var descriptor in descriptors)
        {
            var id = (string)descriptor.GetType().GetProperty("OperationId")!.GetValue(descriptor)!;
            if (id.StartsWith("classify.", StringComparison.Ordinal))
            {
                classifyIds.Add(id);
            }
        }

        Assert.Equal(12, classifyIds.Count);
        Assert.DoesNotContain(classifyIds, id => id.Contains("http", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(classifyIds, id => id.Contains("endpoint", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(classifyIds, id => id.Contains("watch", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(classifyIds, id => id.Contains("daemon", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Graph_quality_artifacts_exist()
    {
        var root = RepositoryRoot();
        Assert.True(File.Exists(Path.Combine(root, "scripts", "verify-classify-graph.sh")));
        Assert.True(File.Exists(Path.Combine(root, "docs", "verification", "classify-graph.md")));
        Assert.True(File.Exists(Path.Combine(root, ".lexicon", "graph", "CLASSIFY", "module.json")));
    }

    /// <summary>
    /// Every raw SHA-256/size the static report records must match the current artifact bytes.
    /// The Markdown report must not claim a raw self-hash (impossible for a file that embeds it).
    /// Live report hash is emitted by scripts/verify-classify-graph.sh only.
    /// </summary>
    [Fact]
    public void Recorded_immutable_input_fingerprints_match_live_artifacts()
    {
        var root = RepositoryRoot();
        var reportPath = Path.Combine(root, "docs", "verification", "classify-graph.md");
        var report = File.ReadAllText(reportPath);

        var rowPattern = new Regex(
            @"^\|\s*`([^`]+)`\s*\|\s*`([0-9a-f]{64})`\s*\|\s*(\d+)\s*\|$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        var rows = rowPattern.Matches(report);
        Assert.True(rows.Count > 0, "classify-graph.md must record immutable-input fingerprint rows");

        var required = new HashSet<string>(StringComparer.Ordinal)
        {
            "scripts/verify-classify-graph.sh",
            "tests/Tally.Tests/Classify/ClassifyGraphEvidenceGuardTests.cs",
            ".lexicon/graph/CLASSIFY/module.json"
        };
        const string forbiddenSelf = "docs/verification/classify-graph.md";
        var found = new Dictionary<string, (string Digest, int Bytes)>(StringComparer.Ordinal);

        foreach (Match match in rows)
        {
            var path = match.Groups[1].Value;
            var digest = match.Groups[2].Value;
            var bytes = int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.False(
                string.Equals(path, forbiddenSelf, StringComparison.Ordinal),
                "report must not embed its own raw self-hash/size");
            found[path] = (digest, bytes);
        }

        Assert.True(
            required.SetEquals(found.Keys),
            "recorded fingerprint paths drift — expected exactly: "
                + string.Join(", ", required.Order(StringComparer.Ordinal))
                + "; found: "
                + string.Join(", ", found.Keys.Order(StringComparer.Ordinal)));

        foreach (var (relative, expected) in found.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var absolute = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(absolute), "missing recorded artifact: " + relative);
            var data = File.ReadAllBytes(absolute);
            var liveDigest = Convert.ToHexStringLower(SHA256.HashData(data));
            Assert.Equal(expected.Digest, liveDigest);
            Assert.Equal(expected.Bytes, data.Length);
        }

        // Document the live report hash policy in the report body (no embedded self-hash).
        Assert.Contains(
            "must not embed its own raw self-hash",
            report,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "scripts/verify-classify-graph.sh",
            report,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_module_identity_is_classify()
    {
        var root = RepositoryRoot();
        var modulePath = Path.Combine(root, ".lexicon", "graph", "CLASSIFY", "module.json");
        var text = File.ReadAllText(modulePath);
        Assert.Contains("\"code\": \"CLASSIFY\"", text, StringComparison.Ordinal);
        Assert.Contains("Transaction Classification", text, StringComparison.Ordinal);
        // Privacy: module description may mention domains abstractly, but this guard only
        // fingerprints identity fields — never private fixture rows or financial payloads.
        Assert.DoesNotContain("SELECT ", text, StringComparison.OrdinalIgnoreCase);
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
