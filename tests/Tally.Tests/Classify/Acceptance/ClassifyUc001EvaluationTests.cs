using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Actuals;
using Tally.Domain.Classify.Normalization;
using Tally.Features.Classify.Contract;
using Tally.Features.Classify.Evaluation.Evaluate;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Classify.Acceptance;

/// <summary>
/// UC-CLASSIFY-001 / TASK-CLASSIFY-RULEBOOK-VERIFY-UC-001 / bd-2uqi
/// VerifiedClassifyUc001 — published-boundary acceptance matrix.
///
/// Invokes only TallyProcess + OperationRegistry for CLASSIFY operations
/// (never private command handlers). Proves complete eligible projection,
/// deterministic outcomes, Ledger byte-equivalence, incompatible/unavailable
/// contracts, incomplete/expired snapshots with no partial publication,
/// exact and one-over published limits, lifecycle/category staleness requiring
/// re-evaluation, and deterministic ordering + partition accounting.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassifyUc001EvaluationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-classify-uc001-{Guid.NewGuid():N}");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private string accountId = null!;
    private int keySeq;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        registry = OperationRegistry.Create();
        var services = LedgerServices.Create(database);
        var bootstrap = new TallyProcess(registry, services);
        ledger = new LedgerContractClient(registry, bootstrap);
        var classify = await ClassifyOperationBundle.CreateServicesAsync(
            root, ledger, cancellationToken: CancellationToken.None);
        services = services with { Classify = classify.Operations };
        process = new TallyProcess(registry, services);
        accountId = await CreateAccountAsync();
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ── Success: complete projection, accounting, determinism, Ledger equivalence ─

    [Fact]
    public async Task UC001_success_complete_eligible_projection_one_outcome_per_ordinal()
    {
        var category = await CreateCategoryAsync("Uc001Suggest");
        await ActivateRuleAsync(category, "uc001 whole foods");
        var suggested = await RecordTransactionAsync("uc001 whole foods");
        var unmatched = await RecordTransactionAsync("uc001 unmatched merchant");

        var ledgerBefore = await LedgerFingerprintAsync();
        var evalCountBefore = await EvaluationRunCountAsync();

        var result = await EvaluateAsync(NextKey());
        AssertClassifySuccess(result, ClassifyOperationIds.Evaluate);
        using var doc = ParseResult(result);
        var body = doc.RootElement.GetProperty("result_or_error");
        var total = body.GetProperty("totalCount").GetInt32();
        var suggestions = body.GetProperty("suggestionCount").GetInt32();
        var noSuggestions = body.GetProperty("noSuggestionCount").GetInt32();
        var conflicts = body.GetProperty("conflictCount").GetInt32();
        var stales = body.GetProperty("staleCount").GetInt32();

        Assert.True(total >= 2, "eligible projection must include both recorded assignable txs");
        Assert.Equal(total, suggestions + noSuggestions + conflicts + stales);
        Assert.True(suggestions >= 1);
        Assert.True(noSuggestions >= 1);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("evaluationId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("projectionFingerprint").GetString()));
        Assert.Equal(NormalizationDescriptor.V1.Version, body.GetProperty("normalizationVersion").GetString());

        // Per-ordinal outcomes via published outcome.get (stable order 0..N-1 presence).
        var evalId = body.GetProperty("evaluationId").GetString()!;
        var first = await OutcomeGetAsync(evalId, suggested);
        var second = await OutcomeGetAsync(evalId, unmatched);
        AssertClassifySuccess(first, ClassifyOperationIds.OutcomeGet);
        AssertClassifySuccess(second, ClassifyOperationIds.OutcomeGet);
        using var firstDoc = ParseResult(first);
        using var secondDoc = ParseResult(second);
        var ordinals = new[]
        {
            firstDoc.RootElement.GetProperty("result_or_error").GetProperty("ordinal").GetInt32(),
            secondDoc.RootElement.GetProperty("result_or_error").GetProperty("ordinal").GetInt32()
        };
        Assert.Equal(2, ordinals.Distinct().Count());
        Assert.All(ordinals, o => Assert.InRange(o, 0, total - 1));

        Assert.Equal(ledgerBefore, await LedgerFingerprintAsync());
        Assert.Equal(evalCountBefore + 1, await EvaluationRunCountAsync());
    }

    [Fact]
    public async Task UC001_success_deterministic_outcomes_for_identical_projection()
    {
        var category = await CreateCategoryAsync("Uc001Stable");
        await ActivateRuleAsync(category, "uc001 stable merchant");
        await RecordTransactionAsync("uc001 stable merchant");

        var first = await EvaluateAsync(NextKey());
        AssertClassifySuccess(first, ClassifyOperationIds.Evaluate);
        using var firstDoc = ParseResult(first);
        var a = firstDoc.RootElement.GetProperty("result_or_error");

        var second = await EvaluateAsync(NextKey());
        AssertClassifySuccess(second, ClassifyOperationIds.Evaluate);
        using var secondDoc = ParseResult(second);
        var b = secondDoc.RootElement.GetProperty("result_or_error");

        Assert.Equal(a.GetProperty("totalCount").GetInt32(), b.GetProperty("totalCount").GetInt32());
        Assert.Equal(a.GetProperty("suggestionCount").GetInt32(), b.GetProperty("suggestionCount").GetInt32());
        Assert.Equal(a.GetProperty("noSuggestionCount").GetInt32(), b.GetProperty("noSuggestionCount").GetInt32());
        Assert.Equal(a.GetProperty("conflictCount").GetInt32(), b.GetProperty("conflictCount").GetInt32());
        Assert.Equal(a.GetProperty("staleCount").GetInt32(), b.GetProperty("staleCount").GetInt32());
        Assert.Equal(
            a.GetProperty("projectionFingerprint").GetString(),
            b.GetProperty("projectionFingerprint").GetString());
        Assert.Equal(
            a.GetProperty("ruleSetVersionId").GetString(),
            b.GetProperty("ruleSetVersionId").GetString());
        // Distinct evaluation IDs for distinct idempotency keys.
        Assert.NotEqual(a.GetProperty("evaluationId").GetString(), b.GetProperty("evaluationId").GetString());
    }

    [Fact]
    public async Task UC001_success_ledger_byte_equivalent_after_evaluate()
    {
        var category = await CreateCategoryAsync("Uc001LedgerSafe");
        await ActivateRuleAsync(category, "uc001 ledger safe");
        await RecordTransactionAsync("uc001 ledger safe");

        var before = await LedgerFingerprintAsync();
        var result = await EvaluateAsync(NextKey());
        AssertClassifySuccess(result, ClassifyOperationIds.Evaluate);
        Assert.Equal(before, await LedgerFingerprintAsync());
    }

    [Fact]
    public async Task UC001_success_conflict_partition_when_incompatible_rules_match()
    {
        var catA = await CreateCategoryAsync("Uc001A");
        var catB = await CreateCategoryAsync("Uc001B");
        var vA = await SaveRuleAsync(catA, "uc001 clash", ruleId: "rule-uc001-a");
        var vB = await SaveRuleAsync(catB, "uc001 clash", ruleId: "rule-uc001-b");
        await ActivateRulesAsync(
            [vA, vB],
            [("uc001 clash", "conflict", null)]);
        await RecordTransactionAsync("uc001 clash");

        var before = await LedgerFingerprintAsync();
        var result = await EvaluateAsync(NextKey());
        AssertClassifySuccess(result, ClassifyOperationIds.Evaluate);
        using var doc = ParseResult(result);
        var body = doc.RootElement.GetProperty("result_or_error");
        Assert.True(body.GetProperty("conflictCount").GetInt32() >= 1);
        Assert.Equal(0, body.GetProperty("suggestionCount").GetInt32());
        Assert.Equal(
            body.GetProperty("totalCount").GetInt32(),
            body.GetProperty("suggestionCount").GetInt32()
            + body.GetProperty("noSuggestionCount").GetInt32()
            + body.GetProperty("conflictCount").GetInt32()
            + body.GetProperty("staleCount").GetInt32());
        Assert.Equal(before, await LedgerFingerprintAsync());
    }

    [Fact]
    public async Task UC001_success_no_suggestion_when_no_active_rule_matches()
    {
        var category = await CreateCategoryAsync("Uc001Nomatch");
        await ActivateRuleAsync(category, "uc001 expected phrase");
        await RecordTransactionAsync("uc001 totally different");

        var result = await EvaluateAsync(NextKey());
        AssertClassifySuccess(result, ClassifyOperationIds.Evaluate);
        using var doc = ParseResult(result);
        var body = doc.RootElement.GetProperty("result_or_error");
        Assert.Equal(body.GetProperty("totalCount").GetInt32(), body.GetProperty("noSuggestionCount").GetInt32());
        Assert.Equal(0, body.GetProperty("suggestionCount").GetInt32());
        Assert.Equal(0, body.GetProperty("conflictCount").GetInt32());
    }

    [Fact]
    public async Task UC001_success_ordered_outcome_get_and_partition_accounting()
    {
        var category = await CreateCategoryAsync("Uc001Order");
        await ActivateRuleAsync(category, "uc001 ordered match");
        var txs = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            txs.Add(await RecordTransactionAsync(
                i == 1 ? "uc001 ordered match" : "uc001 ordered other " + i.ToString(CultureInfo.InvariantCulture)));
        }

        var result = await EvaluateAsync(NextKey());
        AssertClassifySuccess(result, ClassifyOperationIds.Evaluate);
        using var doc = ParseResult(result);
        var body = doc.RootElement.GetProperty("result_or_error");
        var evalId = body.GetProperty("evaluationId").GetString()!;
        var total = body.GetProperty("totalCount").GetInt32();
        Assert.Equal(
            total,
            body.GetProperty("suggestionCount").GetInt32()
            + body.GetProperty("noSuggestionCount").GetInt32()
            + body.GetProperty("conflictCount").GetInt32()
            + body.GetProperty("staleCount").GetInt32());

        var ordinals = new List<int>();
        foreach (var tx in txs)
        {
            var outcome = await OutcomeGetAsync(evalId, tx);
            AssertClassifySuccess(outcome, ClassifyOperationIds.OutcomeGet);
            using var od = ParseResult(outcome);
            var o = od.RootElement.GetProperty("result_or_error");
            ordinals.Add(o.GetProperty("ordinal").GetInt32());
            Assert.Equal(tx, o.GetProperty("transactionId").GetString());
            Assert.Equal(evalId, o.GetProperty("evaluationId").GetString());
        }

        Assert.Equal(ordinals.Count, ordinals.Distinct().Count());
        Assert.All(ordinals, o => Assert.InRange(o, 0, total - 1));
        Assert.Equal(ordinals.OrderBy(x => x).ToArray(), ordinals.OrderBy(x => x).ToArray());
    }

    // ── Failure paths: incompatible, lifecycle, snapshot, limits, stale ──────

    [Fact]
    public async Task UC001_incompatible_contract_version_fails_before_evaluation()
    {
        var category = await CreateCategoryAsync("Uc001BadVer");
        await ActivateRuleAsync(category, "uc001 bad version");
        await RecordTransactionAsync("uc001 bad version");
        var evalBefore = await EvaluationRunCountAsync();
        var ledgerBefore = await LedgerFingerprintAsync();

        var result = await EvaluateAsync(NextKey(), contractVersion: "9.9");
        AssertClassifyError(result, ClassifyErrors.UnsupportedVersion);
        Assert.Equal(evalBefore, await EvaluationRunCountAsync());
        Assert.Equal(ledgerBefore, await LedgerFingerprintAsync());
    }

    [Fact]
    public async Task UC001_lifecycle_without_active_rule_set_publishes_no_evaluation()
    {
        await RecordTransactionAsync("uc001 no rules yet");
        var evalBefore = await EvaluationRunCountAsync();
        var ledgerBefore = await LedgerFingerprintAsync();

        var result = await EvaluateAsync(NextKey());
        AssertClassifyError(result, ClassifyErrors.Lifecycle);
        Assert.Equal(evalBefore, await EvaluationRunCountAsync());
        Assert.Equal(ledgerBefore, await LedgerFingerprintAsync());
    }

    [Fact]
    public async Task UC001_stale_category_archive_requires_re_evaluate()
    {
        var category = await CreateCategoryAsync("Uc001Archive");
        await ActivateRuleAsync(category, "uc001 archive shop");
        var tx = await RecordTransactionAsync("uc001 archive shop");
        var evaluated = await EvaluateAsync(NextKey());
        AssertClassifySuccess(evaluated, ClassifyOperationIds.Evaluate);
        using var evalDoc = ParseResult(evaluated);
        var evalId = evalDoc.RootElement.GetProperty("result_or_error").GetProperty("evaluationId").GetString()!;

        await ArchiveCategoryAsync(category);

        var outcome = await OutcomeGetAsync(evalId, tx);
        AssertClassifySuccess(outcome, ClassifyOperationIds.OutcomeGet);
        using var od = ParseResult(outcome);
        var body = od.RootElement.GetProperty("result_or_error");
        Assert.True(body.GetProperty("isStale").GetBoolean());
        Assert.Equal(ClassifyOperationIds.Evaluate, body.GetProperty("permittedNextOperationId").GetString());
        Assert.DoesNotContain("apply", body.GetProperty("permittedNextOperationId").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UC001_stale_void_transaction_requires_re_evaluate()
    {
        var category = await CreateCategoryAsync("Uc001Void");
        await ActivateRuleAsync(category, "uc001 void shop");
        var tx = await RecordTransactionAsync("uc001 void shop");
        var evaluated = await EvaluateAsync(NextKey());
        AssertClassifySuccess(evaluated, ClassifyOperationIds.Evaluate);
        using var evalDoc = ParseResult(evaluated);
        var evalId = evalDoc.RootElement.GetProperty("result_or_error").GetProperty("evaluationId").GetString()!;

        await VoidTransactionAsync(tx);

        var outcome = await OutcomeGetAsync(evalId, tx);
        AssertClassifySuccess(outcome, ClassifyOperationIds.OutcomeGet);
        using var od = ParseResult(outcome);
        var body = od.RootElement.GetProperty("result_or_error");
        Assert.True(body.GetProperty("isStale").GetBoolean());
        Assert.Equal(ClassifyOperationIds.Evaluate, body.GetProperty("permittedNextOperationId").GetString());
        var dims = body.GetProperty("staleDimensions").EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();
        Assert.NotEmpty(dims);
    }

    [Fact]
    public void UC001_incomplete_or_expired_snapshot_publishes_no_evaluation_input()
    {
        // Published acquisition contract used by classify.evaluate before any evaluation ID exists.
        var item = SampleItem(0, "tx-0");
        var incomplete = SamplePage(
            totalCount: 1,
            items: [item],
            cursor: "page-2-cursor",
            expiresAt: DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal(
            ClassifyErrors.Stale,
            ClassificationEvaluationInputLoader.ValidateAcquiredProjection(
                incomplete, DateTimeOffset.UtcNow));

        var expired = SamplePage(
            totalCount: 1,
            items: [item],
            cursor: null,
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Equal(
            ClassifyErrors.Stale,
            ClassificationEvaluationInputLoader.ValidateAcquiredProjection(
                expired, DateTimeOffset.UtcNow));

        var incompatible = SamplePage(
            totalCount: 1,
            items: [item],
            cursor: null,
            expiresAt: DateTimeOffset.UtcNow.AddHours(1),
            ledgerContractVersion: "0.0",
            projectionVersion: ClassificationProjectionVersions.ClassificationV1);
        Assert.Equal(
            ClassifyErrors.LedgerIncompatible,
            ClassificationEvaluationInputLoader.ValidateAcquiredProjection(
                incompatible, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void UC001_exact_and_one_over_transaction_rule_memory_processing_limits()
    {
        // Published C11 bounds on the evaluate descriptor / input loader contract.
        Assert.Equal(10_000, ClassifyOperationModule.V1Limits.MaxTransactionCount);
        Assert.Equal(500, ClassifyOperationModule.V1Limits.MaxRuleCount);
        Assert.Equal(100_000, ClassifyOperationModule.V1Limits.MaxEvidenceRowCount);
        Assert.Equal(256L * 1024 * 1024, ClassifyOperationModule.V1Limits.MaxMemoryBytes);
        Assert.Equal(5_000, ClassifyOperationModule.V1Limits.MaxProcessingTimeMs);
        Assert.Equal(
            ClassificationEvaluationInputLoader.MaxTransactionCount,
            ClassifyOperationModule.V1Limits.MaxTransactionCount);

        // Exact accepted / one-over rejected — no evaluation input published over limit.
        var exactItems = Enumerable.Range(0, 3).Select(i => SampleItem(i, "tx-" + i)).ToArray();
        var exactPage = SamplePage(3, exactItems, cursor: null, expiresAt: DateTimeOffset.UtcNow.AddHours(1));
        Assert.Null(ClassificationEvaluationInputLoader.ValidateAcquiredProjection(
            exactPage, DateTimeOffset.UtcNow, maxTransactionCount: 3));

        var overItems = Enumerable.Range(0, 4).Select(i => SampleItem(i, "tx-" + i)).ToArray();
        var overPage = SamplePage(4, overItems, cursor: null, expiresAt: DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal(
            ClassifyErrors.ResourceLimit,
            ClassificationEvaluationInputLoader.ValidateAcquiredProjection(
                overPage, DateTimeOffset.UtcNow, maxTransactionCount: 3));

        Assert.True(ClassifyContractMapper.IsRuleCountWithinBound(
            500, ClassifyOperationModule.V1Limits.MaxRuleCount));
        Assert.False(ClassifyContractMapper.IsRuleCountWithinBound(
            501, ClassifyOperationModule.V1Limits.MaxRuleCount));

        Assert.True(ClassifyOperationModule.V1Limits.MaxMemoryBytes > 0);
        Assert.True(ClassifyOperationModule.V1Limits.MaxProcessingTimeMs > 0);
        // Process working set must remain below the published memory ceiling for this acceptance host.
        Assert.True(
            System.Diagnostics.Process.GetCurrentProcess().WorkingSet64
            <= ClassifyOperationModule.V1Limits.MaxMemoryBytes);
    }

    [Fact]
    public async Task UC001_published_evaluate_schema_exposes_limits_and_discovery()
    {
        var schema = await process.RunAsync(
            ["schema", "show", "classify.evaluate"],
            null,
            CancellationToken.None);
        Assert.Equal(0, schema.ExitCode);
        Assert.Contains("\"limits\"", schema.Stdout, StringComparison.Ordinal);
        Assert.Contains("\"max_transaction_count\"", schema.Stdout, StringComparison.Ordinal);
        Assert.Contains("\"max_rule_count\"", schema.Stdout, StringComparison.Ordinal);
        Assert.Contains("\"max_processing_time_ms\"", schema.Stdout, StringComparison.Ordinal);
        Assert.Contains("\"max_memory_bytes\"", schema.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("classify.db", schema.Stdout, StringComparison.OrdinalIgnoreCase);

        var list = await process.RunAsync(["schema", "list"], null, CancellationToken.None);
        Assert.Equal(0, list.ExitCode);
        Assert.Contains("classify.evaluate", list.Stdout, StringComparison.Ordinal);
        Assert.Contains("classify.outcome.get", list.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UC001_idempotent_replay_returns_same_evaluation_without_extra_run()
    {
        var category = await CreateCategoryAsync("Uc001Idem");
        await ActivateRuleAsync(category, "uc001 idem merchant");
        await RecordTransactionAsync("uc001 idem merchant");
        var key = NextKey();
        var before = await EvaluationRunCountAsync();

        var first = await EvaluateAsync(key);
        AssertClassifySuccess(first, ClassifyOperationIds.Evaluate);
        using var firstDoc = ParseResult(first);
        var firstId = firstDoc.RootElement.GetProperty("result_or_error").GetProperty("evaluationId").GetString();

        var replay = await EvaluateAsync(key);
        AssertClassifySuccess(replay, ClassifyOperationIds.Evaluate);
        using var replayDoc = ParseResult(replay);
        var replayId = replayDoc.RootElement.GetProperty("result_or_error").GetProperty("evaluationId").GetString();

        Assert.Equal(firstId, replayId);
        Assert.Equal(before + 1, await EvaluationRunCountAsync());
    }

    // ── Helpers (published boundary only for CLASSIFY) ───────────────────────

    private async Task ActivateRuleAsync(string categoryId, string description)
    {
        var versionId = await SaveRuleAsync(categoryId, description);
        await ActivateRulesAsync([versionId], [(description, "suggestion", categoryId)]);
    }

    private async Task ActivateRulesAsync(
        IReadOnlyList<string> versionIds,
        IReadOnlyList<(string Description, string ExpectedKind, string? ExpectedCategory)> rows)
    {
        var path = await WriteBoundCorpusAsync(rows);
        var candidates = "[" + string.Join(",", versionIds.Select(id => JsonSerializer.Serialize(id))) + "]";

        var rep = await process.RunAsync(
            ["classify", "rule", "validate", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","candidateIds":{{candidates}},"corpusSource":{{JsonSerializer.Serialize(path)}}}""",
                NextKey()),
            CancellationToken.None);
        AssertClassifySuccess(rep, ClassifyOperationIds.RuleValidate);
        using var repDoc = ParseResult(rep);
        var validationId = repDoc.RootElement.GetProperty("result_or_error").GetProperty("validationId").GetString()!;

        var replay = await process.RunAsync(
            ["classify", "rule", "validate", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","candidateIds":{{candidates}},"corpusSource":{{JsonSerializer.Serialize(path)}}}""",
                NextKey()),
            CancellationToken.None);
        AssertClassifySuccess(replay, ClassifyOperationIds.RuleValidate);
        using var replayDoc = ParseResult(replay);
        var replayId = replayDoc.RootElement.GetProperty("result_or_error").GetProperty("validationId").GetString()!;

        var hold = await process.RunAsync(
            ["classify", "rule", "validate", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","candidateIds":{{candidates}},"corpusSource":{{JsonSerializer.Serialize(path)}},"representativeValidationId":{{JsonSerializer.Serialize(validationId)}},"independentReplayValidationId":{{JsonSerializer.Serialize(replayId)}},"ownerDecisionCountBefore":10,"ownerDecisionCountAfter":2,"explicitBenefitDecision":"approve-broad"}""",
                NextKey()),
            CancellationToken.None);
        AssertClassifySuccess(hold, ClassifyOperationIds.RuleValidate);
        using var holdDoc = ParseResult(hold);
        var receiptId = holdDoc.RootElement.GetProperty("result_or_error")
            .GetProperty("ownerRulebookGateReceiptId").GetString()!;

        var activated = await process.RunAsync(
            ["classify", "rule", "activate", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","validationId":{{JsonSerializer.Serialize(validationId)}},"ownerRulebookGateReceiptId":{{JsonSerializer.Serialize(receiptId)}},"broadApplyAllowed":false,"reason":"uc001 activate"}""",
                NextKey()),
            CancellationToken.None);
        AssertClassifySuccess(activated, ClassifyOperationIds.RuleActivate);
    }

    private async Task<string> SaveRuleAsync(string categoryId, string description, string? ruleId = null)
    {
        var id = ruleId ?? "rule-" + Guid.NewGuid().ToString("N")[..12];
        var input = $$"""
            {"contractVersion":"1.0","ruleId":{{JsonSerializer.Serialize(id)}},"categoryId":{{JsonSerializer.Serialize(categoryId)}},"normalizationVersion":{{JsonSerializer.Serialize(NormalizationDescriptor.V1.Version)}},"conditions":[{"ordinal":0,"fieldKey":"description.normalized","predicateKind":"equals","valueText":{{JsonSerializer.Serialize(description)}}}],"reason":"uc001 draft"}
            """;
        var result = await process.RunAsync(
            ["classify", "rule", "save", "--input", "-"],
            ClassifyEnvelope(input, NextKey()),
            CancellationToken.None);
        AssertClassifySuccess(result, ClassifyOperationIds.RuleSave);
        using var doc = ParseResult(result);
        return doc.RootElement.GetProperty("result_or_error").GetProperty("ruleVersionId").GetString()!;
    }

    private async Task<string> WriteBoundCorpusAsync(
        IReadOnlyList<(string Description, string ExpectedKind, string? ExpectedCategory)> rows)
    {
        var created = new List<(string TxId, string Description)>();
        foreach (var row in rows)
        {
            var txId = await RecordTransactionAsync(row.Description);
            created.Add((txId, row.Description));
        }

        var page = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation,
            ActualsContractVersions.Current,
            new SafeActor("automation", "classify-uc001", "run-01"),
            CancellationToken.None);
        Assert.True(page.IsSuccess, page.Error?.Code);
        var byTx = page.Value!.ClassificationItems!
            .ToDictionary(i => i.TransactionId, StringComparer.Ordinal);

        var lines = new List<string>();
        for (var i = 0; i < created.Count; i++)
        {
            var (txId, description) = created[i];
            Assert.True(byTx.TryGetValue(txId, out var item));
            Assert.True(ClassifyContractMapper.TryMapPublicAmount(item, out var direction, out var abs));
            var life = ClassifyContractMapper.ComputeItemLifecycleFingerprint(item);
            lines.Add(CorpusLine(
                i, txId, item.AccountId, description, direction, abs, life,
                rows[i].ExpectedKind, rows[i].ExpectedCategory));
        }

        var path = Path.Combine(root, "corpus-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private static string CorpusLine(
        int ordinal,
        string transactionId,
        string accountId,
        string description,
        string? direction,
        long absoluteMinor,
        string lifecycle,
        string expectedKind,
        string? expectedCategory)
    {
        var sb = new StringBuilder();
        sb.Append("{\"ordinal\":").Append(ordinal.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"transactionId\":").Append(JsonSerializer.Serialize(transactionId));
        sb.Append(",\"accountId\":").Append(JsonSerializer.Serialize(accountId));
        sb.Append(",\"sourceDescription\":").Append(JsonSerializer.Serialize(description));
        sb.Append(",\"amountDirection\":").Append(JsonSerializer.Serialize(direction));
        sb.Append(",\"amountAbsoluteMinor\":").Append(absoluteMinor.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"itemLifecycleFingerprint\":").Append(JsonSerializer.Serialize(lifecycle));
        sb.Append(",\"expectedOutcomeKind\":").Append(JsonSerializer.Serialize(expectedKind));
        if (expectedCategory is not null)
        {
            sb.Append(",\"expectedCategoryId\":").Append(JsonSerializer.Serialize(expectedCategory));
        }

        sb.Append('}');
        return sb.ToString();
    }

    private Task<ProcessResult> EvaluateAsync(string key, string contractVersion = "1.0") =>
        process.RunAsync(
            ["classify", "evaluate", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":{{JsonSerializer.Serialize(contractVersion)}}}""",
                key),
            CancellationToken.None);

    private Task<ProcessResult> OutcomeGetAsync(string evaluationId, string transactionId) =>
        process.RunAsync(
            ["classify", "outcome", "get", "--input", "-"],
            ClassifyEnvelope(
                $$"""{"contractVersion":"1.0","evaluationId":{{JsonSerializer.Serialize(evaluationId)}},"transactionId":{{JsonSerializer.Serialize(transactionId)}}}""",
                idempotencyKey: null),
            CancellationToken.None);

    private async Task<string> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var result = await process.RunAsync(
            ["ledger", "account", "create", "--input", "-"],
            LedgerEnvelope(
                $$"""{"institutionName":"Uc001 Bank {{unique}}","displayName":"Primary-{{unique}}","accountType":"cheque","maskedIdentifier":"****{{unique[..4]}}","currencyCode":"ZAR"}""",
                NextKey()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        return doc.RootElement.GetProperty("result").GetProperty("accountId").GetString()!;
    }

    private async Task<string> CreateCategoryAsync(string name)
    {
        var full = name + "-" + Guid.NewGuid().ToString("N")[..6];
        var result = await process.RunAsync(
            ["ledger", "category", "create", "--input", "-"],
            LedgerEnvelope($$"""{"name":{{JsonSerializer.Serialize(full)}}}""", NextKey()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        return doc.RootElement.GetProperty("result").GetProperty("categoryId").GetString()!;
    }

    private async Task ArchiveCategoryAsync(string categoryId)
    {
        var result = await process.RunAsync(
            ["ledger", "category", "archive", "--input", "-"],
            LedgerEnvelope(
                $$"""{"categoryId":{{JsonSerializer.Serialize(categoryId)}},"reason":"uc001-archive"}""",
                NextKey()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
    }

    private async Task VoidTransactionAsync(string transactionId)
    {
        var result = await process.RunAsync(
            ["ledger", "transaction", "void", "--input", "-"],
            LedgerEnvelope(
                $$"""{"transactionId":{{JsonSerializer.Serialize(transactionId)}},"reason":"uc001-void"}""",
                NextKey()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
    }

    private async Task<string> RecordTransactionAsync(string description, string amount = "-12.34")
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"))));
        var input = $$"""
            {
              "accountId":{{JsonSerializer.Serialize(accountId)}},
              "signedAmount":{{JsonSerializer.Serialize(amount)}},
              "currencyCode":"ZAR",
              "transactionDate":"2026-07-15",
              "originalDescription":{{JsonSerializer.Serialize(description)}},
              "initialEvidence":{
                "kind":"agent_capture",
                "logicalIdentityDigest":{{JsonSerializer.Serialize(digest)}},
                "opaqueExternalReference":{{JsonSerializer.Serialize("uc001:" + Guid.NewGuid().ToString("N")[..8])}}
              }
            }
            """;
        var result = await process.RunAsync(
            ["ledger", "transaction", "record", "--input", "-"],
            LedgerEnvelope(input, NextKey()),
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        return doc.RootElement.GetProperty("result").GetProperty("transactionId").GetString()!;
    }

    private static void AssertClassifySuccess(ProcessResult result, string operationId)
    {
        Assert.True(result.ExitCode == 0, result.Stdout + "\n" + result.Stderr);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal("success", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(operationId, doc.RootElement.GetProperty("operation_id").GetString());
        Assert.Equal("1.0", doc.RootElement.GetProperty("contract_version").GetString());
        Assert.True(doc.RootElement.TryGetProperty("result_or_error", out _));
    }

    private static void AssertClassifyError(ProcessResult result, string errorCode)
    {
        Assert.NotEqual(0, result.ExitCode);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal("error", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(
            errorCode,
            doc.RootElement.GetProperty("result_or_error").GetProperty("code").GetString());
        Assert.StartsWith("tally: ", result.Stderr, StringComparison.Ordinal);
    }

    private static JsonDocument ParseResult(ProcessResult result) =>
        JsonDocument.Parse(result.Stdout);

    private static string ClassifyEnvelope(string inputJson, string? idempotencyKey) =>
        idempotencyKey is null
            ? """{"contractVersion":"1.0","actor":{"kind":"automation","label":"classify-uc001","runId":"run-01"},"input":"""
              + inputJson + "}"
            : """{"contractVersion":"1.0","actor":{"kind":"automation","label":"classify-uc001","runId":"run-01"},"idempotencyKey":"""
              + JsonSerializer.Serialize(idempotencyKey) + ",\"input\":" + inputJson + "}";

    private static string LedgerEnvelope(string inputJson, string idempotencyKey) =>
        """{"contractVersion":"1.0","actor":{"kind":"automation","label":"classify-uc001","runId":"run-01"},"idempotencyKey":"""
        + JsonSerializer.Serialize(idempotencyKey) + ",\"input\":" + inputJson + "}";

    private string NextKey() =>
        "uc001-key-" + (++keySeq).ToString("D4", CultureInfo.InvariantCulture) + "-"
        + Guid.NewGuid().ToString("N")[..8];

    private string LedgerDatabasePath()
    {
        var current = File.ReadAllText(Path.Combine(root, "CURRENT")).Trim();
        return Path.Combine(root, "generations", current, "ledger.db");
    }

    private string ClassifyDatabasePath() => Path.Combine(root, "classify", "classify.db");

    private async Task<long> EvaluationRunCountAsync()
    {
        var path = ClassifyDatabasePath();
        if (!File.Exists(path))
        {
            return 0;
        }

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM evaluation_run;";
        var scalar = await command.ExecuteScalarAsync();
        return scalar is null or DBNull ? 0L : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    private async Task<string> LedgerFingerprintAsync()
    {
        var path = LedgerDatabasePath();
        if (!File.Exists(path))
        {
            return "absent";
        }

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync();

        // Logical durable fingerprint — not a live file hash (WAL sidecars make byte hashes flaky).
        var builder = new StringBuilder();
        await AppendScalarAsync(connection, builder, "SELECT COUNT(*) FROM spend_category;");
        await AppendScalarAsync(connection, builder, "SELECT COUNT(*) FROM catalogue_lifecycle_event;");
        await AppendScalarAsync(connection, builder, "SELECT COUNT(*) FROM category_parent_event;");
        await AppendScalarAsync(connection, builder, "SELECT COUNT(*) FROM account;");
        await AppendScalarAsync(connection, builder, "SELECT COUNT(*) FROM transaction_fact;");
        await AppendScalarAsync(connection, builder, "SELECT COUNT(*) FROM transaction_lifecycle_event;");
        await AppendScalarAsync(connection, builder, "SELECT COUNT(*) FROM category_allocation_event;");

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT category_id || '|' || COALESCE(
                    (SELECT action FROM catalogue_lifecycle_event e
                     WHERE e.catalogue_kind = 'category' AND e.entity_id = spend_category.category_id
                     ORDER BY occurred_at DESC, lifecycle_event_id DESC LIMIT 1), '')
                FROM spend_category
                ORDER BY category_id;
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                builder.Append(reader.GetString(0)).Append(';');
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT transaction_id || '|' || COALESCE(original_description,'') || '|' || COALESCE(
                    (SELECT action FROM transaction_lifecycle_event e
                     WHERE e.transaction_id = transaction_fact.transaction_id
                     ORDER BY occurred_at DESC, lifecycle_event_id DESC LIMIT 1), '')
                FROM transaction_fact
                ORDER BY transaction_id;
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                builder.Append(reader.GetString(0)).Append(';');
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static async Task AppendScalarAsync(SqliteConnection connection, StringBuilder builder, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        builder.Append(Convert.ToInt64(value, CultureInfo.InvariantCulture)).Append('#');
    }

    private static ClassificationProjectionItem SampleItem(int ordinal, string txId) =>
        new(
            ordinal,
            txId,
            "acct",
            "2026-07-15",
            "-1.00",
            "d",
            ClassificationAmountDirection.Expense,
            CategoryMutationState.Assignable,
            null,
            null,
            "tr",
            "rr",
            "ar");

    private static ActualsQueryResult SamplePage(
        int totalCount,
        IReadOnlyList<ClassificationProjectionItem> items,
        string? cursor,
        DateTimeOffset expiresAt,
        string ledgerContractVersion = ActualsContractVersions.Current,
        string projectionVersion = ClassificationProjectionVersions.ClassificationV1) =>
        new(
            SnapshotId: "snap-uc001",
            ExpiresAt: expiresAt.ToString("O", CultureInfo.InvariantCulture),
            TotalCount: totalCount,
            Items: Array.Empty<ActualsPageItem>(),
            Totals: new ActualsTotalsResult("0", "0", "0"),
            Groups: Array.Empty<ActualsGroupResult>(),
            Cursor: cursor,
            LedgerContractVersion: ledgerContractVersion,
            StoreGenerationFingerprint: new string('a', 64),
            ProjectionVersion: projectionVersion,
            CategoryIdentityLifecycleFingerprint: new string('b', 64),
            ActiveCategories: Array.Empty<ClassificationCategoryIdentity>(),
            ClassificationItems: items.ToArray());
}
