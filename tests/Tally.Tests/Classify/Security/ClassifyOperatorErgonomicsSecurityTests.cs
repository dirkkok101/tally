using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Classify;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Classify.Rules;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Classify.Normalization;
using Tally.Features.Classify.Contract;
using Tally.Features.Classify.Corpus.Build;
using Tally.Infrastructure.Classify.Corpus;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Classify.Security;

/// <summary>
/// TASK-CLASSIFY-ERGONOMICS-PRIVACY-RECOVERY-GATE / bd-3mdk —
/// Cross-operation privacy, filesystem, crash, cursor, stale, dual no-mutation,
/// composition authority, and offline isolation matrix for the five ergonomics ops.
/// Disposable 0700 synthetic roots only — never live TALLY_DATA_ROOT / financial data.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassifyOperatorErgonomicsSecurityTests : IAsyncLifetime
{
    private const string DescriptionCanary = "CANARY_ERGONOMICS_DESC_PRIVATE_zzz";

    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-erg-sec-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("automation", "ergonomics-security", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private ClassifyServices services = null!;
    private string accountId = null!;
    private int keySeq;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        registry = OperationRegistry.Create();
        process = new TallyProcess(registry, LedgerServices.Create(database));
        ledger = new LedgerContractClient(registry, process);
        services = await ClassifyOperationBundle.CreateServicesAsync(root, ledger, cancellationToken: CancellationToken.None);
        accountId = (await CreateAccountAsync()).AccountId;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ── Privacy: allowed stdout vs forbidden sinks ───────────────────────────

    [Fact]
    public async Task TC_ERGONOMICS_PRIVACY_unresolved_stdout_may_expose_owner_normalized_description()
    {
        var seeded = await SeedNoSuggestionAsync(DescriptionCanary, count: 2);
        var result = await services.UnresolvedReport.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, 10, 2),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var json = JsonSerializer.Serialize(result.Value, ClassifyJsonContext.Default.ClassifyUnresolvedReportResult);
        Assert.NotEmpty(result.Value!.Groups);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.Groups[0].RepresentativeNormalizedDescription));
        // Raw canary / transaction IDs / absolute path must not re-enter the typed result.
        Assert.DoesNotContain(DescriptionCanary, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceDescription", json, StringComparison.OrdinalIgnoreCase);
        foreach (var tx in seeded.TransactionIds)
        {
            Assert.DoesNotContain(tx, json, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(root, json, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/ubuntu/.local/share/tally", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TC_ERGONOMICS_PRIVACY_forbidden_sinks_exclude_canaries_after_unresolved_report()
    {
        var seeded = await SeedNoSuggestionAsync(DescriptionCanary, count: 2);
        var result = await services.UnresolvedReport.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, 10, 2),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE sql LIKE $pat;";
        cmd.Parameters.AddWithValue("$pat", "%" + DescriptionCanary + "%");
        var hits = Convert.ToInt64(await cmd.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture);
        Assert.Equal(0, hits);
        AssertForbiddenInTrackedRepoSources(DescriptionCanary);
        _ = seeded;
    }

    [Fact]
    public async Task TC_ERGONOMICS_LOGGING_cursor_bytes_exclude_descriptions_and_paths()
    {
        var seeded = await SeedMixedSuggestionAsync(DescriptionCanary);
        var page = await services.OutcomeList.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", seeded.EvaluationId, 1),
            actor,
            CancellationToken.None);
        Assert.True(page.IsSuccess, page.ErrorCode);
        Assert.NotNull(page.Value!.Continuation);
        var cursor = page.Value.Continuation!;
        Assert.DoesNotContain(DescriptionCanary, cursor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(root, cursor, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceDescription", cursor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/home/ubuntu/.local/share/tally", cursor, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TC_ERGONOMICS_PERSISTENCE_corpus_receipt_excludes_destination_and_labels()
    {
        var destParent = Path.Combine(root, "corpus-out");
        Directory.CreateDirectory(destParent);
        File.SetUnixFileMode(destParent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var dest = Path.Combine(destParent, "ok.jsonl");
        var result = await services.CorpusBuild.HandleAsync(
            CorpusRequest(dest, [new ClassifyCorpusBuildLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)], [ProjectionItem("tx-1", 0, DescriptionCanary)]),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var json = JsonSerializer.Serialize(result.Value, ClassifyJsonContext.Default.ClassifyCorpusBuildResult);
        Assert.DoesNotContain(dest, json, StringComparison.Ordinal);
        Assert.DoesNotContain(DescriptionCanary, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tx-1", json, StringComparison.Ordinal);
        Assert.True(File.Exists(dest));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(dest));
    }

    // ── Filesystem attacks ───────────────────────────────────────────────────

    [Fact]
    public async Task TC_ERGONOMICS_FILESYSTEM_symlink_destination_fails_without_write()
    {
        var parent = Path.Combine(root, "sym-parent");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var real = Path.Combine(parent, "real.jsonl");
        await File.WriteAllTextAsync(real, "existing");
        File.SetUnixFileMode(real, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var link = Path.Combine(parent, "link.jsonl");
        Assert.Equal(0, Symlink(real, link));
        var before = await File.ReadAllTextAsync(real);
        var result = await services.CorpusBuild.HandleAsync(
            CorpusRequest(link, [new ClassifyCorpusBuildLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)], [ProjectionItem("tx-1", 0)]),
            actor,
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(before, await File.ReadAllTextAsync(real));
        Assert.DoesNotContain(real, result.ErrorCode ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TC_ERGONOMICS_FILESYSTEM_hard_linked_destination_is_not_overwritten()
    {
        var parent = Path.Combine(root, "hard-parent");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var a = Path.Combine(parent, "a.jsonl");
        var b = Path.Combine(parent, "b.jsonl");
        await File.WriteAllTextAsync(a, "HARD_LINK_CANARY_CONTENT");
        File.SetUnixFileMode(a, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal(0, Link(a, b));
        Assert.True(LstatNlink(b) >= 2);
        var result = await services.CorpusBuild.HandleAsync(
            CorpusRequest(b, [new ClassifyCorpusBuildLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)], [ProjectionItem("tx-1", 0)]),
            actor,
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("HARD_LINK_CANARY_CONTENT", await File.ReadAllTextAsync(a));
        Assert.Equal("HARD_LINK_CANARY_CONTENT", await File.ReadAllTextAsync(b));
    }

    [Fact]
    public async Task TC_ERGONOMICS_FILESYSTEM_group_writable_parent_fails_privacy()
    {
        var badParent = Path.Combine(root, "group-parent");
        Directory.CreateDirectory(badParent);
        File.SetUnixFileMode(
            badParent,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
        var dest = Path.Combine(badParent, "out.jsonl");
        var result = await services.CorpusBuild.HandleAsync(
            CorpusRequest(dest, [new ClassifyCorpusBuildLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)], [ProjectionItem("tx-1", 0)]),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.PrivacyRejected, result.ErrorCode);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task TC_ERGONOMICS_FILESYSTEM_outside_temp_root_relative_path_fails()
    {
        var result = await services.CorpusBuild.HandleAsync(
            CorpusRequest(
                "relative-not-absolute.jsonl",
                [new ClassifyCorpusBuildLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)],
                [ProjectionItem("tx-1", 0)]),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.PrivacyRejected, result.ErrorCode);
    }

    [Fact]
    public async Task TC_ERGONOMICS_FILESYSTEM_existing_different_destination_is_never_replaced()
    {
        var parent = Path.Combine(root, "exist-parent");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var dest = Path.Combine(parent, "exists.jsonl");
        await File.WriteAllTextAsync(dest, "DIFFERENT_EXISTING_CONTENT");
        File.SetUnixFileMode(dest, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var result = await services.CorpusBuild.HandleAsync(
            CorpusRequest(dest, [new ClassifyCorpusBuildLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)], [ProjectionItem("tx-1", 0)]),
            actor,
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("DIFFERENT_EXISTING_CONTENT", await File.ReadAllTextAsync(dest));
    }

    [Fact]
    public async Task TC_ERGONOMICS_FILESYSTEM_oversized_label_count_fails_without_destination()
    {
        var parent = Path.Combine(root, "oversize-parent");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var dest = Path.Combine(parent, "over.jsonl");
        var labels = Enumerable.Range(0, 10_001)
            .Select(i => new ClassifyCorpusBuildLabel("tx-" + i, ClassifyOutcomeKind.NoSuggestion))
            .ToArray();
        var items = Enumerable.Range(0, 10_001)
            .Select(i => ProjectionItem("tx-" + i, i))
            .ToArray();
        var result = await services.CorpusBuild.HandleAsync(
            CorpusRequest(dest, labels, items),
            actor,
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.False(File.Exists(dest));
    }

    // ── Crash / recovery ─────────────────────────────────────────────────────

    [Fact]
    public async Task TC_ERGONOMICS_CRASH_failed_build_leaves_no_recognized_temp_or_destination()
    {
        var parent = Path.Combine(root, "crash-parent");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var dest = Path.Combine(parent, "crash.jsonl");
        var result = await services.CorpusBuild.HandleAsync(
            CorpusRequest(dest, Array.Empty<ClassifyCorpusBuildLabel>(), [ProjectionItem("tx-1", 0)]),
            actor,
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.False(File.Exists(dest));
        Assert.Empty(Directory.GetFiles(parent, PrivateCorpusWriter.RecognizedTempPrefix + "*"));
    }

    [Fact]
    public async Task TC_ERGONOMICS_CRASH_successful_build_clears_recognized_temps()
    {
        var parent = Path.Combine(root, "ok-crash-parent");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var dest = Path.Combine(parent, "ok.jsonl");
        var result = await services.CorpusBuild.HandleAsync(
            CorpusRequest(dest, [new ClassifyCorpusBuildLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)], [ProjectionItem("tx-1", 0)]),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.True(File.Exists(dest));
        Assert.Empty(Directory.GetFiles(parent, PrivateCorpusWriter.RecognizedTempPrefix + "*"));
    }

    // ── Cursor / stale ───────────────────────────────────────────────────────

    [Fact]
    public async Task TC_ERGONOMICS_CURSOR_malformed_continuation_fails_closed_with_null_result()
    {
        var seeded = await SeedMixedSuggestionAsync("cursor shop");
        var before = await CaptureOraclesAsync();
        var result = await services.OutcomeList.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", seeded.EvaluationId, 10, Continuation: "%%%not-valid%%%"),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.CursorInvalid, result.ErrorCode);
        Assert.Null(result.Value);
        await AssertNoMutationAsync(before);
    }

    [Fact]
    public async Task TC_ERGONOMICS_CURSOR_bytes_are_integrity_checked_not_raw_json_payload()
    {
        var seeded = await SeedMixedSuggestionAsync("cursor integrity");
        var page = await services.OutcomeList.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", seeded.EvaluationId, 1),
            actor,
            CancellationToken.None);
        Assert.True(page.IsSuccess, page.ErrorCode);
        Assert.NotNull(page.Value!.Continuation);
        Assert.False(page.Value.Continuation!.StartsWith("{", StringComparison.Ordinal));
        Assert.DoesNotContain("evaluationId", page.Value.Continuation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TC_ERGONOMICS_STALE_voided_transaction_fails_unresolved_without_writes()
    {
        var seeded = await SeedNoSuggestionAsync("void coffee", count: 2);
        await VoidTransactionAsync(seeded.TransactionIds[0]);
        var before = await CaptureOraclesAsync();
        var result = await services.UnresolvedReport.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, 10, 2),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.Stale, result.ErrorCode);
        Assert.Null(result.Value);
        await AssertNoMutationAsync(before);
    }

    [Fact]
    public async Task TC_ERGONOMICS_STALE_missing_evaluation_fails_outcome_list_without_writes()
    {
        var before = await CaptureOraclesAsync();
        var result = await services.OutcomeList.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", "missing-eval-id", 10),
            actor,
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        await AssertNoMutationAsync(before);
    }

    // ── Dual no-mutation oracles ─────────────────────────────────────────────

    [Fact]
    public async Task TC_ERGONOMICS_NO_MUTATION_query_failure_preserves_classify_db_hash()
    {
        _ = await SeedNoSuggestionAsync("hash coffee", count: 2);
        var beforeHash = await HashClassifyOracleAsync();
        var beforeLedger = await CaptureLedgerOracleAsync();
        var result = await services.UnresolvedReport.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", "missing", 10, 2),
            actor,
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(beforeHash, await HashClassifyOracleAsync());
        Assert.Equal(beforeLedger, await CaptureLedgerOracleAsync());
    }

    [Fact]
    public async Task TC_ERGONOMICS_NO_MUTATION_successful_queries_do_not_mutate_ledger_allocations()
    {
        var seeded = await SeedNoSuggestionAsync("ledger nomut coffee", count: 2);
        var beforeLedger = await CaptureLedgerOracleAsync();
        Assert.True((await services.OutcomeList.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", seeded.EvaluationId, 50), actor, CancellationToken.None)).IsSuccess);
        Assert.True((await services.RuleList.HandleAsync(
            new ClassifyRuleListRequest("1.0", 50), actor, CancellationToken.None)).IsSuccess);
        Assert.True((await services.RuleSetActiveGet.HandleAsync(
            new ClassifyRuleSetActiveGetRequest("1.0"), actor, CancellationToken.None)).IsSuccess);
        Assert.True((await services.UnresolvedReport.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, 10, 2), actor, CancellationToken.None)).IsSuccess);
        Assert.Equal(beforeLedger, await CaptureLedgerOracleAsync());
    }

    [Fact]
    public async Task TC_ERGONOMICS_NO_MUTATION_corpus_success_only_creates_authorized_destination()
    {
        var parent = Path.Combine(root, "auth-dest");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var dest = Path.Combine(parent, "authorized.jsonl");
        var beforeLedger = await CaptureLedgerOracleAsync();
        Assert.Empty(Directory.GetFiles(parent, "*", SearchOption.AllDirectories));

        var result = await services.CorpusBuild.HandleAsync(
            CorpusRequest(dest, [new ClassifyCorpusBuildLabel("tx-1", ClassifyOutcomeKind.NoSuggestion)], [ProjectionItem("tx-1", 0)]),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(
            [dest],
            Directory.GetFiles(parent, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(beforeLedger, await CaptureLedgerOracleAsync());
    }

    // ── Composition / preview authority ──────────────────────────────────────

    [Fact]
    public async Task TC_ERGONOMICS_COMPOSITION_empty_selected_outcomes_is_rejected_without_ledger_mutation()
    {
        var seeded = await SeedMixedSuggestionAsync("preview auth");
        var beforeLedger = await CaptureLedgerOracleAsync();
        var result = await services.Preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                "1.0",
                seeded.EvaluationId,
                new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes)),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(beforeLedger, await CaptureLedgerOracleAsync());
    }

    [Fact]
    public async Task TC_ERGONOMICS_COMPOSITION_outcome_list_ids_compose_selected_outcomes_preview()
    {
        var seeded = await SeedMixedSuggestionAsync("compose preview phrase");
        var page = await services.OutcomeList.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", seeded.EvaluationId, 50),
            actor,
            CancellationToken.None);
        Assert.True(page.IsSuccess, page.ErrorCode);
        var suggestionIds = page.Value!.Items
            .Where(i => i.Kind == ClassifyOutcomeKind.Suggestion)
            .Select(i => i.OutcomeId)
            .Take(5)
            .ToArray();
        Assert.NotEmpty(suggestionIds);
        var beforeLedger = await CaptureLedgerOracleAsync();
        var previewResult = await services.Preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                "1.0",
                seeded.EvaluationId,
                new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes, OutcomeIds: suggestionIds)),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(previewResult.IsSuccess, previewResult.ErrorCode);
        Assert.Equal(beforeLedger, await CaptureLedgerOracleAsync());
    }

    // ── Isolation / offline ──────────────────────────────────────────────────

    [Fact]
    public void TC_ERGONOMICS_ISOLATION_ergonomics_composition_has_no_network_or_plugin_surface()
    {
        var repositoryRoot = RepositoryRoot();
        string[] paths =
        [
            Path.Combine(repositoryRoot, "src", "Tally", "Features", "Classify", "Unresolved"),
            Path.Combine(repositoryRoot, "src", "Tally", "Features", "Classify", "Corpus"),
            Path.Combine(repositoryRoot, "src", "Tally", "Features", "Classify", "Rules", "Discovery"),
            Path.Combine(repositoryRoot, "src", "Tally", "Features", "Classify", "Evaluation", "Outcome"),
            Path.Combine(repositoryRoot, "src", "Tally", "Domain", "Classify", "Discovery"),
            Path.Combine(repositoryRoot, "src", "Tally", "Domain", "Classify", "Unresolved"),
            Path.Combine(repositoryRoot, "src", "Tally", "Bootstrap", "Features", "ClassifyExtensions.cs")
        ];
        var composition = string.Join(
            '\n',
            paths.SelectMany(path => Directory.Exists(path)
                    ? Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)
                    : File.Exists(path) ? [path] : Array.Empty<string>())
                .Order(StringComparer.Ordinal)
                .Select(File.ReadAllText));

        string[] forbidden =
        [
            "HttpClient", "HttpListener", "TcpListener", "WebApplication", "UseKestrel",
            "Assembly.LoadFrom", "Assembly.Load(", "Process.Start", "OpenAI", "Anthropic",
            "GrpcChannel", "WebSocket", "AddPlugins", "Microsoft.AspNetCore"
        ];
        Assert.All(
            forbidden,
            token => Assert.DoesNotContain(token, composition, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TC_ERGONOMICS_ISOLATION_registry_exposes_five_additive_ops_without_background_aliases()
    {
        var ids = OperationRegistry.Create().Descriptors
            .Select(d => d.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(ClassifyOperationIds.OutcomeList, ids);
        Assert.Contains(ClassifyOperationIds.RuleList, ids);
        Assert.Contains(ClassifyOperationIds.RuleSetActiveGet, ids);
        Assert.Contains(ClassifyOperationIds.CorpusBuild, ids);
        Assert.Contains(ClassifyOperationIds.UnresolvedReport, ids);
        Assert.DoesNotContain("classify.watch", ids);
        Assert.DoesNotContain("classify.sync", ids);
        Assert.DoesNotContain("classify.daemon", ids);
        Assert.DoesNotContain("classify.webhook", ids);
        Assert.DoesNotContain("classify.invoke", ids);
        Assert.Equal(17, ids.Count(id => id.StartsWith("classify.", StringComparison.Ordinal)));
        Assert.Equal(105, OperationRegistry.Create().Descriptors.Count);
    }

    [Fact]
    public void TC_ERGONOMICS_ISOLATION_descriptor_discovery_opens_no_data_root()
    {
        var registryLocal = OperationRegistry.Create();
        var bare = LedgerServices.Create();
        foreach (var id in new[]
                 {
                     ClassifyOperationIds.OutcomeList,
                     ClassifyOperationIds.RuleList,
                     ClassifyOperationIds.RuleSetActiveGet,
                     ClassifyOperationIds.CorpusBuild,
                     ClassifyOperationIds.UnresolvedReport
                 })
        {
            var handler = registryLocal.Find(id)!.HandlerFactory(bare, registryLocal);
            Assert.NotEqual("FoundationOperationHandler", handler.GetType().Name);
        }
    }

    [Fact]
    public void TC_ERGONOMICS_ISOLATION_live_tally_data_root_is_never_the_fixture_root()
    {
        Assert.DoesNotContain("/home/ubuntu/.local/share/tally", root, StringComparison.Ordinal);
        Assert.StartsWith(Path.GetTempPath(), root, StringComparison.Ordinal);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(root));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed record SeededEval(string EvaluationId, IReadOnlyList<string> TransactionIds);

    private async Task<SeededEval> SeedNoSuggestionAsync(string phrase, int count)
    {
        var category = await CreateCategoryAsync("NS");
        var rulePhrase = "never-match-" + Guid.NewGuid().ToString("N")[..8];
        var versionId = await SaveDraftAsync(category.CategoryId, rulePhrase);
        await ActivateWithGateAsync(versionId, category.CategoryId, rulePhrase);
        var txs = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            txs.Add((await RecordAsync(phrase)).TransactionId);
        }

        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);
        return new SeededEval(evaluated.Value!.EvaluationId, txs);
    }

    private async Task<SeededEval> SeedMixedSuggestionAsync(string matchPhrase)
    {
        var category = await CreateCategoryAsync("Mix");
        var versionId = await SaveDraftAsync(category.CategoryId, matchPhrase);
        await ActivateWithGateAsync(versionId, category.CategoryId, matchPhrase);
        var matched = await RecordAsync(matchPhrase);
        var unmatched = await RecordAsync("unmatched " + Guid.NewGuid().ToString("N")[..6]);
        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);
        return new SeededEval(evaluated.Value!.EvaluationId, [matched.TransactionId, unmatched.TransactionId]);
    }

    private async Task<(long EvalRuns, long Outcomes, long Evidence, string? ActiveRuleSet, string ClassifyHash, string LedgerOracle)> CaptureOraclesAsync()
    {
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        async Task<long> Count(string table)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM " + table + ";";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture);
        }

        await using var activeCmd = connection.CreateCommand();
        activeCmd.CommandText = "SELECT rule_set_version_id FROM active_rule_set LIMIT 1;";
        var activeId = (await activeCmd.ExecuteScalarAsync(CancellationToken.None)) as string;

        return (
            await Count("evaluation_run"),
            await Count("classification_outcome"),
            await Count("match_evidence"),
            activeId,
            await HashClassifyOracleAsync(),
            await CaptureLedgerOracleAsync());
    }

    private async Task AssertNoMutationAsync(
        (long EvalRuns, long Outcomes, long Evidence, string? ActiveRuleSet, string ClassifyHash, string LedgerOracle) before)
    {
        var after = await CaptureOraclesAsync();
        Assert.Equal(before.EvalRuns, after.EvalRuns);
        Assert.Equal(before.Outcomes, after.Outcomes);
        Assert.Equal(before.Evidence, after.Evidence);
        Assert.Equal(before.ActiveRuleSet, after.ActiveRuleSet);
        Assert.Equal(before.ClassifyHash, after.ClassifyHash);
        Assert.Equal(before.LedgerOracle, after.LedgerOracle);
    }

    private async Task<string> HashClassifyOracleAsync()
    {
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        async Task<long> Count(string table)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM " + table + ";";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture);
        }

        await using var activeCmd = connection.CreateCommand();
        activeCmd.CommandText = "SELECT rule_set_version_id FROM active_rule_set LIMIT 1;";
        var activeId = (await activeCmd.ExecuteScalarAsync(CancellationToken.None)) as string ?? "";
        var payload = string.Join(
            "|",
            await Count("evaluation_run"),
            await Count("classification_outcome"),
            await Count("match_evidence"),
            await Count("operation_idempotency"),
            await Count("apply_preview"),
            activeId);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private async Task<string> CaptureLedgerOracleAsync()
    {
        var projection = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation,
            ActualsContractVersions.Current,
            actor,
            CancellationToken.None);
        Assert.True(projection.IsSuccess, projection.Error?.Code);
        return string.Join(
            "|",
            projection.Value!.StoreGenerationFingerprint,
            projection.Value.TotalCount,
            (projection.Value.ActiveCategories ?? Array.Empty<ClassificationCategoryIdentity>()).Count);
    }

    private static void AssertForbiddenInTrackedRepoSources(string canary)
    {
        var repo = RepositoryRoot();
        foreach (var path in new[] { Path.Combine(repo, "docs"), Path.Combine(repo, "scripts") })
        {
            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*.md", SearchOption.AllDirectories)
                         .Concat(Directory.EnumerateFiles(path, "*.sh", SearchOption.AllDirectories)))
            {
                Assert.DoesNotContain(canary, File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Tally.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    private static ClassifyCorpusBuildRequest CorpusRequest(
        string dest,
        IReadOnlyList<ClassifyCorpusBuildLabel> labels,
        IReadOnlyList<ClassificationProjectionItem> items) =>
        new(
            "1.0",
            "idem-" + Guid.NewGuid().ToString("N"),
            dest,
            new ClassifyCorpusBuildProjectionEnvelope(
                ActualsContractVersions.Current,
                ClassificationProjectionVersions.ClassificationV1,
                new string('a', 64),
                "snap-1",
                "2026-08-02T12:00:00.0000000Z",
                new string('b', 64),
                NormalizationDescriptor.V1.Version,
                items),
            labels);

    private static ClassificationProjectionItem ProjectionItem(
        string txId,
        int ordinal,
        string description = "merchant") =>
        new(
            Ordinal: ordinal,
            TransactionId: txId,
            AccountId: "acct-1",
            EffectiveDate: "2026-07-15",
            SignedAmount: "-12.34",
            SourceDescription: description,
            AmountDirection: ClassificationAmountDirection.Expense,
            CategoryMutationState: CategoryMutationState.Assignable,
            CurrentCategoryId: null,
            CurrentAllocationId: null,
            TransactionRevision: "tr-" + ordinal,
            RelationshipRevision: "rr-" + ordinal,
            AllocationRevision: "ar-" + ordinal);

    private string NextKey() => "erg-sec-key-" + (++keySeq).ToString(CultureInfo.InvariantCulture);

    private async Task<AccountDetail> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        return await ExecuteSuccessAsync(
            "ledger.account.create",
            new CreateAccountInput(
                "ErgSec Bank " + unique,
                "P-" + unique,
                AccountType.Cheque,
                "****" + ((int)((uint)unique.GetHashCode() % 9000u) + 1000).ToString(CultureInfo.InvariantCulture),
                "ZAR"),
            NextKey(),
            LedgerJsonContext.Default.CreateAccountInput,
            LedgerJsonContext.Default.AccountDetail);
    }

    private Task<CategoryDetail> CreateCategoryAsync(string name) =>
        ExecuteSuccessAsync(
            "ledger.category.create",
            new CreateCategoryInput(name + "-" + Guid.NewGuid().ToString("N")[..6]),
            NextKey(),
            LedgerJsonContext.Default.CreateCategoryInput,
            LedgerJsonContext.Default.CategoryDetail);

    private async Task<TransactionDetail> RecordAsync(string description)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"))));
        return await ExecuteSuccessAsync(
            "ledger.transaction.record",
            new RecordTransactionInput(
                accountId,
                "-12.34",
                "ZAR",
                "2026-07-15",
                null,
                description,
                null,
                null,
                new RegisterEvidenceInput(EvidenceKind.AgentCapture, digest, "erg-sec:" + Guid.NewGuid().ToString("N")[..8], null, null)),
            NextKey(),
            LedgerJsonContext.Default.RecordTransactionInput,
            LedgerJsonContext.Default.TransactionDetail);
    }

    private async Task VoidTransactionAsync(string transactionId)
    {
        var descriptor = registry.Find("ledger.transaction.void");
        if (descriptor is null)
        {
            return;
        }

        var request = new RequestEnvelope(
            "1.0",
            actor,
            JsonSerializer.SerializeToElement(
                new VoidTransactionInput(transactionId, "erg-sec-void"),
                TransactionCorrectionJsonContext.Default.VoidTransactionInput),
            NextKey());
        var json = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var args = descriptor.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Concat(["--input", "-"]).ToArray();
        _ = await process.RunAsync(args, json, CancellationToken.None);
    }

    private async Task<string> SaveDraftAsync(string categoryId, string description)
    {
        var result = await services.Save.HandleAsync(
            new ClassifyRuleSaveRequest(
                ClassifyOperationIds.ContractVersion,
                "rule-" + Guid.NewGuid().ToString("N")[..12],
                null,
                categoryId,
                NormalizationDescriptor.V1.Version,
                [
                    new ClassificationRuleConditionInput(
                        0,
                        ClassificationRuleFieldKey.DescriptionNormalized,
                        ClassificationRulePredicateKind.Equals,
                        ValueText: description)
                ],
                "erg-sec draft"),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!.RuleVersionId;
    }

    private async Task ActivateWithGateAsync(string versionId, string categoryId, string description)
    {
        var path = await WriteBoundCorpusAsync([(description, "suggestion", categoryId)]);
        var rep = await services.Validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(rep.IsSuccess, rep.ErrorCode);
        var replay = await services.Validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(replay.IsSuccess, replay.ErrorCode);
        var hold = await services.Validate.HandleAsync(
            new ClassifyRuleValidateRequest(
                ClassifyOperationIds.ContractVersion, [versionId], path,
                rep.Value!.ValidationId, replay.Value!.ValidationId,
                10, 2, ExplicitBenefitDecision: "approve-broad"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(hold.IsSuccess, hold.ErrorCode);
        var activated = await services.Activate.HandleAsync(
            new ClassifyRuleActivateRequest(
                ClassifyOperationIds.ContractVersion,
                rep.Value.ValidationId,
                hold.Value!.OwnerRulebookGateReceiptId!,
                false,
                "erg-sec activate"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(activated.IsSuccess, activated.ErrorCode);
    }

    private async Task<string> WriteBoundCorpusAsync(
        IReadOnlyList<(string Description, string ExpectedKind, string? ExpectedCategory)> rows)
    {
        var created = new List<(string TxId, string Description)>();
        foreach (var row in rows)
        {
            var tx = await RecordAsync(row.Description);
            created.Add((tx.TransactionId, row.Description));
        }

        var page = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation, ActualsContractVersions.Current, actor, CancellationToken.None);
        Assert.True(page.IsSuccess);
        var byTx = page.Value!.ClassificationItems!.ToDictionary(i => i.TransactionId, StringComparer.Ordinal);
        var lines = new List<string>();
        for (var i = 0; i < created.Count; i++)
        {
            var (txId, description) = created[i];
            var item = byTx[txId];
            Assert.True(ClassifyContractMapper.TryMapPublicAmount(item, out var direction, out var abs));
            var life = ClassifyContractMapper.ComputeItemLifecycleFingerprint(item);
            var sb = new StringBuilder();
            sb.Append("{\"ordinal\":").Append(i.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"transactionId\":").Append(JsonSerializer.Serialize(txId));
            sb.Append(",\"accountId\":").Append(JsonSerializer.Serialize(item.AccountId));
            sb.Append(",\"sourceDescription\":").Append(JsonSerializer.Serialize(description));
            sb.Append(",\"amountDirection\":").Append(JsonSerializer.Serialize(direction));
            sb.Append(",\"amountAbsoluteMinor\":").Append(abs.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"itemLifecycleFingerprint\":").Append(JsonSerializer.Serialize(life));
            sb.Append(",\"expectedOutcomeKind\":").Append(JsonSerializer.Serialize(rows[i].ExpectedKind));
            if (rows[i].ExpectedCategory is not null)
            {
                sb.Append(",\"expectedCategoryId\":").Append(JsonSerializer.Serialize(rows[i].ExpectedCategory));
            }

            sb.Append('}');
            lines.Add(sb.ToString());
        }

        var path = Path.Combine(root, "corpus-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private async Task<TResult> ExecuteSuccessAsync<TInput, TResult>(
        string operationId,
        TInput input,
        string? key,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TInput> inputType,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultType)
    {
        var descriptor = registry.Find(operationId)!;
        var request = new RequestEnvelope("1.0", actor, JsonSerializer.SerializeToElement(input, inputType), key);
        var json = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var args = descriptor.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Concat(["--input", "-"]).ToArray();
        var processResult = await process.RunAsync(args, json, CancellationToken.None);
        Assert.Equal(0, processResult.ExitCode);
        var envelope = JsonSerializer.Deserialize(processResult.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        return JsonSerializer.Deserialize(envelope.Result!.Value, resultType)!;
    }

    private static ulong LstatNlink(string path)
    {
        Assert.Equal(0, Lstat(path, out var st));
        return st.st_nlink;
    }

    [DllImport("libc", EntryPoint = "symlink", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Symlink(string target, string linkpath);

    [DllImport("libc", EntryPoint = "link", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Link(string oldpath, string newpath);

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Lstat(string path, out StatBuf buf);

    [StructLayout(LayoutKind.Sequential)]
    private struct StatBuf
    {
        public ulong st_dev;
        public ulong st_ino;
        public ulong st_nlink;
        public uint st_mode;
        public uint st_uid;
        public uint st_gid;
        public int __pad0;
        public ulong st_rdev;
        public long st_size;
        public long st_blksize;
        public long st_blocks;
        public long st_atim_sec;
        public long st_atim_nsec;
        public long st_mtim_sec;
        public long st_mtim_nsec;
        public long st_ctim_sec;
        public long st_ctim_nsec;
        public long __glibc_reserved1;
        public long __glibc_reserved2;
        public long __glibc_reserved3;
    }
}
