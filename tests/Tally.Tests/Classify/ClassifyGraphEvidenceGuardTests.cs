using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Tally.Cli;
using Tally.Features.Classify.Contract;
using Xunit;

namespace Tally.Tests.Classify;

/// <summary>
/// TASK-CLASSIFY-ERGONOMICS-GATE-MODULE / PAT-CORE-IMPLEMENTATION-PLAN-QUALITY-GATES / bd-2u6r.
/// Named-suite presence, plan/bead tracing, inventory, and forbidden-surface guards for
/// ClassifyGraphQualityEvidence under PLAN-CLASSIFY-OPERATOR-ERGONOMICS-V1.
/// Per-class discovery non-vacuity is asserted by scripts/verify-classify-graph.sh.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassifyGraphEvidenceGuardTests
{
    public const string ErgonomicsPlanRef = "PLAN-CLASSIFY-OPERATOR-ERGONOMICS-V1";
    public const string RulebookPlanRef = "PLAN-CLASSIFY-RULEBOOK-V1";

    /// <summary>
    /// Feature, integration/ledger, security, private-evidence, contract, recovery, storage,
    /// operator ergonomics, and UC suites that must each contribute nonzero discovery.
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
        "OutcomeListTests",
        "OutcomeCursorStalenessTests",
        // Feature — rules
        "ClassificationRuleVocabularyTests",
        "NormalizerV1Tests",
        "RuleActivationTests",
        "RuleDraftPersistenceTests",
        "RuleRetirementTests",
        "SaveClassificationRuleTests",
        "RuleDiscoveryTests",
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
        // Contract / process / ergonomics
        "ClassifyOperationContractTests",
        "ClassifyPublishedContractTests",
        "ClassifyProcessContractTests",
        "ClassifyOperatorErgonomicsContractTests",
        "ClassifyOperatorErgonomicsSecurityTests",
        "ClassifyOperatorErgonomicsProcessTests",
        "ClassifyOperatorBatchPreviewTests",
        "ClassifyCursorCodecTests",
        // Integration — LEDGER public contract
        "ClassifyLedgerBoundaryArchitectureTests",
        "ClassifyLedgerContractClientTests",
        "LedgerClassificationMutationPreconditionTests",
        "LedgerClassificationProjectionTests",
        "LedgerClassifyPrerequisiteTests",
        // Security
        "ClassifyArtifactProtectionTests",
        "ClassifySecurityGateTests",
        // Private-evidence / validation / corpus
        "OwnerRulebookGateTests",
        "ClassificationRuleValidationTests",
        "ClassificationProjectionCorpusMapperTests",
        "PrivateCorpusPrivacyTests",
        "PrivateCorpusReaderTests",
        "PrivateCorpusBuilderTests",
        "PrivateCorpusWriterRecoveryTests",
        "ValidationLimitTests",
        "ValidationPrivacyTests",
        // Unresolved report
        "UnresolvedPatternGroupingPolicyTests",
        "UnresolvedPatternReportTests",
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

    /// <summary>Every ergonomics plan task must appear in graph + bead inventory.</summary>
    public static readonly string[] ErgonomicsTaskRefs =
    [
        "TASK-CLASSIFY-ERGONOMICS-CONTRACT-FOUNDATION",
        "TASK-CLASSIFY-ERGONOMICS-CORPUS-MAPPER",
        "TASK-CLASSIFY-ERGONOMICS-OUTCOME-LIST",
        "TASK-CLASSIFY-ERGONOMICS-RUNTIME-CONVERGENCE",
        "TASK-CLASSIFY-ERGONOMICS-CORPUS-BUILDER",
        "TASK-CLASSIFY-ERGONOMICS-CURSOR-POLICY",
        "TASK-CLASSIFY-ERGONOMICS-PRIVACY-RECOVERY-GATE",
        "TASK-CLASSIFY-ERGONOMICS-RULE-DISCOVERY",
        "TASK-CLASSIFY-ERGONOMICS-BULK-PREVIEW-COMPOSITION",
        "TASK-CLASSIFY-ERGONOMICS-PROCESS-THROUGHPUT-GATE",
        "TASK-CLASSIFY-ERGONOMICS-UNRESOLVED-POLICY",
        "TASK-CLASSIFY-ERGONOMICS-UNRESOLVED-REPORT",
        "TASK-CLASSIFY-ERGONOMICS-GATE-MODULE"
    ];

    /// <summary>Bead IDs compiled for PLAN-CLASSIFY-OPERATOR-ERGONOMICS-V1 (order free).</summary>
    public static readonly string[] ErgonomicsBeadIds =
    [
        "bd-1gly",
        "bd-3k1z",
        "bd-vg33",
        "bd-rly1",
        "bd-1cik",
        "bd-29ch",
        "bd-3mdk",
        "bd-2vbg",
        "bd-wsjo",
        "bd-2byd",
        "bd-elq8",
        "bd-3ciw",
        "bd-2u6r"
    ];

    public static readonly string[] FiveAdditiveOperations =
    [
        ClassifyOperationIds.OutcomeList,
        ClassifyOperationIds.RuleList,
        ClassifyOperationIds.RuleSetActiveGet,
        ClassifyOperationIds.CorpusBuild,
        ClassifyOperationIds.UnresolvedReport
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

    // Private-payload tokens that must never appear as live data in verification companions.
    // Gate scripts may name canary *families* in negative assertions (e.g. CANARY_PROC_); those
    // are metadata, not fixture payloads — forbid only raw financial/private shapes here.
    private static readonly string[] PrivacyForbiddenTokens =
    [
        "sourceDescription",
        "normalized_description",
        "SELECT * FROM",
        "BEGIN RSA PRIVATE",
        "HARD_LINK_CANARY_CONTENT"
    ];

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
    /// suite cannot ship without a discovery floor. Module-final guard lives outside this namespace.
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
    public void Exactly_one_hundred_five_global_and_seventeen_classify_operations_are_published()
    {
        var registry = OperationRegistry.Create();
        Assert.Equal(105, registry.Descriptors.Count);

        var classifyIds = registry.Descriptors
            .Where(d => d.OperationId.StartsWith("classify.", StringComparison.Ordinal))
            .Select(d => d.OperationId)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(17, classifyIds.Length);
        Assert.Equal(17, ClassifyOperationIds.All.Count);
        Assert.All(FiveAdditiveOperations, id => Assert.Contains(id, classifyIds));
        Assert.DoesNotContain(classifyIds, id => id.Contains("http", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(classifyIds, id => id.Contains("endpoint", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(classifyIds, id => id.Contains("watch", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(classifyIds, id => id.Contains("daemon", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Ergonomics_plan_tasks_and_beads_are_present_in_graph()
    {
        var root = RepositoryRoot();
        Assert.True(File.Exists(Path.Combine(
            root, ".lexicon", "graph", "CLASSIFY", "plan", ErgonomicsPlanRef + ".json")));
        Assert.True(File.Exists(Path.Combine(
            root, ".lexicon", "graph", "CLASSIFY", "plan", RulebookPlanRef + ".json")));

        foreach (var task in ErgonomicsTaskRefs)
        {
            Assert.True(
                File.Exists(Path.Combine(root, ".lexicon", "graph", "CLASSIFY", "task", task + ".json")),
                "missing ergonomics task entity " + task);
        }

        // Bead IDs must appear in beads issues export (metadata only — no private payloads).
        var beadsPath = Path.Combine(root, ".beads", "issues.jsonl");
        Assert.True(File.Exists(beadsPath));
        var beadsText = File.ReadAllText(beadsPath);
        foreach (var bead in ErgonomicsBeadIds)
        {
            Assert.Contains(bead, beadsText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Governing_ergonomics_decisions_and_requirements_exist()
    {
        var root = RepositoryRoot();
        string[] decisions =
        [
            "DD-CLASSIFY-OPERATOR-ERGONOMICS-CONTRACT",
            "DD-CLASSIFY-SHIPPED-BASELINE",
            "DD-CLASSIFY-PAGINATED-DISCOVERY",
            "DD-CLASSIFY-PRIVATE-CORPUS-PUBLICATION",
            "DD-CLASSIFY-UNRESOLVED-REPORT-BOUNDARY"
        ];
        foreach (var dd in decisions)
        {
            Assert.True(
                File.Exists(Path.Combine(root, ".lexicon", "graph", "CLASSIFY", "decision", dd + ".json")),
                "missing governing decision " + dd);
        }

        string[] frs =
        [
            "FR-CLASSIFY-OUTCOME-DISCOVERY",
            "FR-CLASSIFY-RULEBOOK-DISCOVERY",
            "FR-CLASSIFY-PRIVATE-CORPUS-BUILDER",
            "FR-CLASSIFY-UNRESOLVED-PATTERN-REPORT",
            "FR-CLASSIFY-BULK-PREVIEW-COMPOSITION"
        ];
        foreach (var fr in frs)
        {
            Assert.True(
                File.Exists(Path.Combine(root, ".lexicon", "graph", "CLASSIFY", "fr", fr + ".json")),
                "missing FR " + fr);
        }
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

    [Fact]
    public void Graph_docs_and_gate_scripts_are_privacy_safe()
    {
        var root = RepositoryRoot();
        // Companion docs must stay free of private payloads. Gate scripts may enumerate
        // forbidden tokens as scan needles — they are not live financial data.
        string[] docPaths =
        [
            Path.Combine(root, "docs", "verification", "classify-graph.md"),
            Path.Combine(root, "docs", "verification", "classify-v1.md")
        ];
        string[] scriptPaths =
        [
            Path.Combine(root, "scripts", "verify-classify-graph.sh"),
            Path.Combine(root, "scripts", "verify-classify-module.sh")
        ];

        foreach (var path in docPaths)
        {
            Assert.True(File.Exists(path), "missing " + path);
            var text = File.ReadAllText(path);
            foreach (var token in PrivacyForbiddenTokens)
            {
                Assert.False(
                    text.Contains(token, StringComparison.Ordinal),
                    $"privacy token '{token}' found in {Relative(root, path)}");
            }

            Assert.DoesNotContain("unbuilt classification", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("unpublished classification_v1", text, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var path in scriptPaths)
        {
            Assert.True(File.Exists(path), "missing " + path);
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("unbuilt classification", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("unpublished classification_v1", text, StringComparison.OrdinalIgnoreCase);
            // Scripts must document never-open live-root policy (path may appear only as guard).
            Assert.True(
                text.Contains("never", StringComparison.OrdinalIgnoreCase)
                || text.Contains("must not", StringComparison.OrdinalIgnoreCase)
                || !text.Contains("/home/ubuntu/.local/share/tally", StringComparison.Ordinal),
                "gate script live-root mention without never-guard: " + Relative(root, path));
        }
    }

    [Fact]
    public void Graph_report_traces_ergonomics_plan_and_inventory()
    {
        var root = RepositoryRoot();
        var report = File.ReadAllText(Path.Combine(root, "docs", "verification", "classify-graph.md"));
        Assert.Contains(ErgonomicsPlanRef, report, StringComparison.Ordinal);
        Assert.Contains("105", report, StringComparison.Ordinal);
        Assert.Contains("17", report, StringComparison.Ordinal);
        Assert.Contains("PLAN-CLASSIFY-OPERATOR-ERGONOMICS-V1", report, StringComparison.Ordinal);
        Assert.Contains("operator ergonomics", report, StringComparison.OrdinalIgnoreCase);
        foreach (var bead in ErgonomicsBeadIds)
        {
            Assert.Contains(bead, report, StringComparison.Ordinal);
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

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}
