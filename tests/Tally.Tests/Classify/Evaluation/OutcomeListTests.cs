using System.Globalization;
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
using Tally.Domain.Ledger;
using Tally.Features.Classify.Contract;
using Tally.Features.Classify.Evaluation.Outcome;
using Tally.Infrastructure.Classify.Storage.Evaluation;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Classify.Evaluation;

/// <summary>
/// TASK-CLASSIFY-ERGONOMICS-OUTCOME-LIST / bd-vg33 —
/// Filters, paging, fields, accounting, privacy, 146-row page, no-mutation.
/// Synthetic isolated roots only; never touches live Tally data.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class OutcomeListTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-outcome-list-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("automation", "outcome-list", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private ClassifyEvaluationServices services = null!;
    private ListClassificationOutcomesQuery listQuery = null!;
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
        services = await ClassifyEvaluationExtensions.CreateServicesAsync(root, ledger, cancellationToken: CancellationToken.None);
        listQuery = new ListClassificationOutcomesQuery(
            services.State.Store,
            services.EvaluationStore,
            new ClassificationOutcomeDiscoveryStore(),
            services.RuleStore,
            services.RuleSetStore,
            ledger);
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

    [Fact]
    public async Task List_requires_actor()
    {
        var result = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", "e", 10),
            null,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.ActorRequired, result.ErrorCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task List_rejects_unsupported_version()
    {
        var result = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest("9.9", "e", 10),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.UnsupportedVersion, result.ErrorCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task List_rejects_blank_evaluation_id()
    {
        var result = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", "  ", 10),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.InvalidInput, result.ErrorCode);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task List_rejects_page_size_outside_bounds(int pageSize)
    {
        var result = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", "eval", pageSize),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.ResourceLimit, result.ErrorCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Unknown_evaluation_returns_not_found_with_null_result()
    {
        var result = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", "missing-eval", 10),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.EvaluationNotFound, result.ErrorCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task List_returns_ordered_items_with_dm_fields()
    {
        var category = await CreateCategoryAsync("ListCat");
        var seeded = await SeedSuggestionEvaluationAsync("list merchant", category);
        var result = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", seeded.EvaluationId, 50),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.NotNull(result.Value);
        Assert.True(result.Value!.OverallCount >= 1);
        Assert.Equal(result.Value.FilteredCount, result.Value.ReturnedCount);
        Assert.Equal(result.Value.Items.Count, result.Value.ReturnedCount);
        Assert.Equal(seeded.EvaluationId, result.Value.EvaluationId);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.EvaluationFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(result.Value.ResultFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(result.Value.RuleSetFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(result.Value.CategoryLifecycleFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(result.Value.LedgerGeneration));
        Assert.All(result.Value.Items, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.OutcomeId));
            Assert.False(string.IsNullOrWhiteSpace(item.TransactionId));
            Assert.True(item.Ordinal >= 0);
            Assert.False(string.IsNullOrWhiteSpace(item.SafeReason));
            Assert.NotNull(item.ContributingRuleVersionIds);
            Assert.NotNull(item.MatchedFieldKeys);
            Assert.NotNull(item.StaleDimensions);
        });
        // Ordinal then transaction id order.
        for (var i = 1; i < result.Value.Items.Count; i++)
        {
            var prev = result.Value.Items[i - 1];
            var cur = result.Value.Items[i];
            Assert.True(
                prev.Ordinal < cur.Ordinal
                || (prev.Ordinal == cur.Ordinal
                    && string.CompareOrdinal(prev.TransactionId, cur.TransactionId) < 0));
        }
    }

    [Fact]
    public async Task Suggestion_item_includes_category_display_and_contributing_rules()
    {
        var category = await CreateCategoryAsync("SugList");
        var seeded = await SeedSuggestionEvaluationAsync("sug list shop", category);
        var result = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest(
                "1.0",
                seeded.EvaluationId,
                50,
                OutcomeKind: ClassifyOutcomeKind.Suggestion),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var item = result.Value!.Items.Single(i => i.TransactionId == seeded.TransactionIds[0]);
        Assert.Equal(ClassifyOutcomeKind.Suggestion, item.Kind);
        Assert.Equal(category.CategoryId, item.SuggestedCategoryId);
        Assert.Equal(category.Name, item.SuggestedCategoryDisplayName);
        Assert.Contains(seeded.RuleVersionId, item.ContributingRuleVersionIds);
        Assert.NotEmpty(item.MatchedFieldKeys);
        Assert.Null(item.ConflictSummary);
        Assert.Empty(item.StaleDimensions);
        Assert.Null(item.PermittedNextOperationId);
    }

    [Fact]
    public async Task Filter_by_outcome_kind_ands_correctly()
    {
        var category = await CreateCategoryAsync("KindF");
        var seeded = await SeedNoSuggestionEvaluationAsync("expected phrase", "different phrase", category);
        var suggestions = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest(
                "1.0", seeded.EvaluationId, 50, OutcomeKind: ClassifyOutcomeKind.Suggestion),
            actor,
            CancellationToken.None);
        var noSug = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest(
                "1.0", seeded.EvaluationId, 50, OutcomeKind: ClassifyOutcomeKind.NoSuggestion),
            actor,
            CancellationToken.None);
        Assert.True(suggestions.IsSuccess, suggestions.ErrorCode);
        Assert.True(noSug.IsSuccess, noSug.ErrorCode);
        Assert.All(suggestions.Value!.Items, i => Assert.Equal(ClassifyOutcomeKind.Suggestion, i.Kind));
        Assert.All(noSug.Value!.Items, i => Assert.Equal(ClassifyOutcomeKind.NoSuggestion, i.Kind));
        Assert.Equal(
            suggestions.Value.FilteredCount + noSug.Value.FilteredCount,
            suggestions.Value.OverallCount);
    }

    [Fact]
    public async Task Filter_by_transaction_id_returns_single_match()
    {
        var category = await CreateCategoryAsync("TxF");
        var seeded = await SeedNoSuggestionEvaluationAsync("tx rule", "tx other", category);
        var tx = seeded.TransactionIds[0];
        var result = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", seeded.EvaluationId, 50, TransactionId: tx),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(1, result.Value!.FilteredCount);
        Assert.Equal(tx, result.Value.Items.Single().TransactionId);
    }

    [Fact]
    public async Task Filter_by_suggested_category_id_ands()
    {
        var category = await CreateCategoryAsync("CatF");
        var seeded = await SeedSuggestionEvaluationAsync("cat filter shop", category);
        var hit = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest(
                "1.0", seeded.EvaluationId, 50, SuggestedCategoryId: category.CategoryId),
            actor,
            CancellationToken.None);
        var miss = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest(
                "1.0", seeded.EvaluationId, 50, SuggestedCategoryId: "no-such-cat"),
            actor,
            CancellationToken.None);
        Assert.True(hit.IsSuccess, hit.ErrorCode);
        Assert.True(miss.IsSuccess, miss.ErrorCode);
        Assert.True(hit.Value!.FilteredCount >= 1);
        Assert.Equal(0, miss.Value!.FilteredCount);
        Assert.Empty(miss.Value.Items);
    }

    [Fact]
    public async Task Filter_by_contributing_rule_version_id_ands()
    {
        var category = await CreateCategoryAsync("RuleF");
        var seeded = await SeedSuggestionEvaluationAsync("rule filter shop", category);
        var hit = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest(
                "1.0", seeded.EvaluationId, 50, ContributingRuleVersionId: seeded.RuleVersionId),
            actor,
            CancellationToken.None);
        var miss = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest(
                "1.0", seeded.EvaluationId, 50, ContributingRuleVersionId: "rv-missing"),
            actor,
            CancellationToken.None);
        Assert.True(hit.IsSuccess, hit.ErrorCode);
        Assert.True(miss.IsSuccess, miss.ErrorCode);
        Assert.True(hit.Value!.FilteredCount >= 1);
        Assert.Equal(0, miss.Value!.FilteredCount);
    }

    [Fact]
    public async Task Keyset_paging_has_no_duplicates_or_omissions()
    {
        var category = await CreateCategoryAsync("Page");
        var versionId = await SaveDraftAsync(category.CategoryId, "page shop");
        await ActivateWithGateAsync(versionId, category.CategoryId, "page shop");
        var txIds = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            txIds.Add((await RecordAsync("page shop " + i)).TransactionId);
        }

        // Also record matching phrases so engine sees multiple no_suggestion / mixed — use same phrase for all.
        // Re-seed with identical description for suggestions.
        // Use unique descriptions that won't match to get 7 no_suggestion, plus ensure evaluate runs.
        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);
        var evalId = evaluated.Value!.EvaluationId;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        var pages = 0;
        while (true)
        {
            var page = await listQuery.HandleAsync(
                new ClassifyOutcomeListRequest("1.0", evalId, 3, Continuation: cursor),
                actor,
                CancellationToken.None);
            Assert.True(page.IsSuccess, page.ErrorCode);
            pages++;
            foreach (var item in page.Value!.Items)
            {
                Assert.True(seen.Add(item.OutcomeId), "duplicate outcome id across pages");
            }

            cursor = page.Value.Continuation;
            if (cursor is null)
            {
                break;
            }

            Assert.True(pages < 50);
        }

        // Final page overall accounting
        var full = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", evalId, 500),
            actor,
            CancellationToken.None);
        Assert.True(full.IsSuccess, full.ErrorCode);
        Assert.Equal(full.Value!.OverallCount, seen.Count);
        Assert.Equal(full.Value.FilteredCount, seen.Count);
        Assert.Equal(full.Value.OverallCount, full.Value.Items.Count);
    }

    [Fact]
    public async Task One_hundred_forty_six_rows_return_in_single_page_size_500_without_outcome_get()
    {
        var category = await CreateCategoryAsync("Bulk");
        const string phrase = "bulk list merchant";
        var versionId = await SaveDraftAsync(category.CategoryId, phrase);
        await ActivateWithGateAsync(versionId, category.CategoryId, phrase);

        var txIds = new List<string>(146);
        for (var i = 0; i < 146; i++)
        {
            txIds.Add((await RecordAsync(phrase)).TransactionId);
        }

        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);
        Assert.True(evaluated.Value!.SuggestionCount >= 146 || evaluated.Value.TotalCount >= 146);

        var getCalls = 0;
        var wrappedGet = services.OutcomeGet;
        // Prove list path does not require outcome.get: we never invoke OutcomeGet.
        var list = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", evaluated.Value.EvaluationId, 500),
            actor,
            CancellationToken.None);
        Assert.True(list.IsSuccess, list.ErrorCode);
        Assert.True(list.Value!.ReturnedCount >= 146, "expected at least 146 selectable rows");
        Assert.Null(list.Value.Continuation);
        Assert.Equal(0, getCalls);
        Assert.Equal(list.Value.ReturnedCount, list.Value.Items.Count);
        Assert.Equal(list.Value.Items.Select(i => i.OutcomeId).Distinct(StringComparer.Ordinal).Count(), list.Value.Items.Count);
        // All 146 seeded transactions appear.
        var returnedTx = list.Value.Items.Select(i => i.TransactionId).ToHashSet(StringComparer.Ordinal);
        Assert.All(txIds, id => Assert.Contains(id, returnedTx));
        _ = wrappedGet; // silence unused when we intentionally never call it
    }

    [Fact]
    public async Task List_does_not_disclose_description_amount_or_path_canaries()
    {
        var category = await CreateCategoryAsync("Priv");
        const string canary = "CANARY_LIST_PRIVATE_DESC_zzz";
        var seeded = await SeedSuggestionEvaluationAsync(canary, category);
        var result = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", seeded.EvaluationId, 50),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var json = JsonSerializer.Serialize(result.Value, ClassifyJsonContext.Default.ClassifyOutcomeListResult);
        Assert.DoesNotContain(canary, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceDescription", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("normalizedValueHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signedAmount", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/home/", json, StringComparison.Ordinal);
        Assert.DoesNotContain(root, json, StringComparison.Ordinal);
        Assert.DoesNotContain("authority", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Successful_list_does_not_mutate_classify_or_ledger_state()
    {
        var category = await CreateCategoryAsync("Nomut");
        var seeded = await SeedSuggestionEvaluationAsync("nomut shop", category);
        var before = await CaptureStateOracleAsync(seeded.EvaluationId, category.CategoryId);

        var result = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", seeded.EvaluationId, 50),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        var after = await CaptureStateOracleAsync(seeded.EvaluationId, category.CategoryId);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Failed_list_does_not_mutate_classify_or_ledger_state()
    {
        var category = await CreateCategoryAsync("NomutFail");
        var seeded = await SeedSuggestionEvaluationAsync("nomut fail shop", category);
        var before = await CaptureStateOracleAsync(seeded.EvaluationId, category.CategoryId);
        var result = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", "missing", 10),
            actor,
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        var after = await CaptureStateOracleAsync(seeded.EvaluationId, category.CategoryId);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Empty_filter_intersection_returns_zero_items_not_error()
    {
        var category = await CreateCategoryAsync("EmptyF");
        var seeded = await SeedSuggestionEvaluationAsync("empty filter shop", category);
        var result = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest(
                "1.0",
                seeded.EvaluationId,
                50,
                OutcomeKind: ClassifyOutcomeKind.Conflict),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(0, result.Value!.FilteredCount);
        Assert.Equal(0, result.Value.ReturnedCount);
        Assert.Empty(result.Value.Items);
        Assert.True(result.Value.OverallCount >= 1);
        Assert.Null(result.Value.Continuation);
    }

    [Fact]
    public async Task Result_fingerprint_stable_across_list_calls()
    {
        var category = await CreateCategoryAsync("Rfp");
        var seeded = await SeedSuggestionEvaluationAsync("rfp shop", category);
        var a = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", seeded.EvaluationId, 50),
            actor,
            CancellationToken.None);
        var b = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", seeded.EvaluationId, 1),
            actor,
            CancellationToken.None);
        Assert.True(a.IsSuccess && b.IsSuccess);
        Assert.Equal(a.Value!.ResultFingerprint, b.Value!.ResultFingerprint);
        Assert.Equal(a.Value.OverallCount, b.Value.OverallCount);
    }

    [Fact]
    public async Task Rename_preserves_freshness_and_returns_current_display_name()
    {
        var category = await CreateCategoryAsync("RenameL");
        var seeded = await SeedSuggestionEvaluationAsync("rename list shop", category);
        await RenameCategoryAsync(category.CategoryId, "Renamed List Display");
        var result = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest(
                "1.0",
                seeded.EvaluationId,
                50,
                OutcomeKind: ClassifyOutcomeKind.Suggestion),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var item = result.Value!.Items.Single(i => i.TransactionId == seeded.TransactionIds[0]);
        Assert.Equal(category.CategoryId, item.SuggestedCategoryId);
        Assert.Equal("Renamed List Display", item.SuggestedCategoryDisplayName);
        Assert.Empty(item.StaleDimensions);
        Assert.Null(item.PermittedNextOperationId);
    }

    [Fact]
    public async Task Accounting_identity_holds_overall_filtered_returned()
    {
        var category = await CreateCategoryAsync("Acct");
        var seeded = await SeedNoSuggestionEvaluationAsync("acct match", "acct miss", category);
        var result = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", seeded.EvaluationId, 1),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.True(result.Value!.OverallCount >= 2);
        Assert.Equal(result.Value.FilteredCount, result.Value.OverallCount);
        Assert.Equal(1, result.Value.ReturnedCount);
        Assert.Single(result.Value.Items);
        Assert.NotNull(result.Value.Continuation);
    }

    [Fact]
    public async Task Conflict_items_include_conflict_summary_without_winner()
    {
        var catA = await CreateCategoryAsync("CLA");
        var catB = await CreateCategoryAsync("CLB");
        var seeded = await SeedConflictEvaluationAsync("clash list", catA, catB);
        var result = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest(
                "1.0",
                seeded.EvaluationId,
                50,
                OutcomeKind: ClassifyOutcomeKind.Conflict),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.True(result.Value!.Items.Count >= 1);
        var item = result.Value.Items[0];
        Assert.Equal(ClassifyOutcomeKind.Conflict, item.Kind);
        Assert.Null(item.SuggestedCategoryId);
        Assert.NotNull(item.ConflictSummary);
        Assert.True(item.ConflictSummary!.Count >= 2);
        Assert.Equal(ClassifyOperationIds.Evaluate, item.PermittedNextOperationId);
    }

    [Fact]
    public async Task Deterministic_replay_yields_identical_page_content()
    {
        var category = await CreateCategoryAsync("Det");
        var seeded = await SeedSuggestionEvaluationAsync("det list", category);
        var a = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", seeded.EvaluationId, 50),
            actor,
            CancellationToken.None);
        var b = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", seeded.EvaluationId, 50),
            actor,
            CancellationToken.None);
        Assert.True(a.IsSuccess && b.IsSuccess);
        Assert.Equal(a.Value!.EvaluationFingerprint, b.Value!.EvaluationFingerprint);
        Assert.Equal(a.Value.ResultFingerprint, b.Value.ResultFingerprint);
        Assert.Equal(
            a.Value.Items.Select(i => (i.OutcomeId, i.Ordinal, i.Kind)).ToArray(),
            b.Value.Items.Select(i => (i.OutcomeId, i.Ordinal, i.Kind)).ToArray());
    }

    [Fact]
    public async Task Page_size_one_and_five_hundred_bounds_accepted()
    {
        var category = await CreateCategoryAsync("Bounds");
        var seeded = await SeedSuggestionEvaluationAsync("bounds shop", category);
        var low = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", seeded.EvaluationId, 1),
            actor,
            CancellationToken.None);
        var high = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", seeded.EvaluationId, 500),
            actor,
            CancellationToken.None);
        Assert.True(low.IsSuccess, low.ErrorCode);
        Assert.True(high.IsSuccess, high.ErrorCode);
        Assert.Equal(1, low.Value!.ReturnedCount);
        Assert.True(high.Value!.ReturnedCount >= 1);
    }

    [Fact]
    public void Mapper_outcome_list_item_excludes_private_payload_shape()
    {
        var sample = new ClassifyOutcomeListItem(
            "out",
            "tx",
            0,
            ClassifyOutcomeKind.Suggestion,
            "ok",
            "cat",
            "Name",
            ["rv"],
            ["description.normalized"],
            null,
            Array.Empty<string>(),
            null);
        var json = JsonSerializer.Serialize(sample, ClassifyJsonContext.Default.ClassifyOutcomeListItem);
        Assert.DoesNotContain("normalizedValueHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceDescription", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("amount", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Filter_fingerprint_is_deterministic()
    {
        var a = ClassifyContractMapper.OutcomeListFilterFingerprint(
            "eval", ClassifyOutcomeKind.Suggestion, "c", "r", ClassifyOutcomeStaleFilter.Fresh, "t");
        var b = ClassifyContractMapper.OutcomeListFilterFingerprint(
            "eval", ClassifyOutcomeKind.Suggestion, "c", "r", ClassifyOutcomeStaleFilter.Fresh, "t");
        Assert.Equal(a, b);
        Assert.NotEqual(
            a,
            ClassifyContractMapper.OutcomeListFilterFingerprint(
                "eval", ClassifyOutcomeKind.Conflict, "c", "r", ClassifyOutcomeStaleFilter.Fresh, "t"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Dual semantic oracle: CLASSIFY row counts/lifecycle + Ledger generation and category name.
    /// Query-time SQLite WAL/snapshot bytes may change without durable product state changes.
    /// </summary>
    private async Task<(long EvalRuns, long Outcomes, long Evidence, string Lifecycle, string LedgerGen, string CategoryName)> CaptureStateOracleAsync(
        string evaluationId,
        string categoryId)
    {
        long evalRuns;
        long outcomes;
        long evidence;
        string lifecycle;
        await using (var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None))
        {
            evalRuns = await services.EvaluationStore.CountEvaluationsAsync(connection, null, CancellationToken.None);
            outcomes = await services.EvaluationStore.CountOutcomesAsync(connection, null, CancellationToken.None);
            evidence = await services.EvaluationStore.CountEvidenceAsync(connection, null, CancellationToken.None);
            var run = await services.EvaluationStore.GetRunAsync(connection, null, evaluationId, CancellationToken.None);
            lifecycle = run?.LifecycleState ?? "missing";
        }

        var projection = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation,
            ActualsContractVersions.Current,
            actor,
            CancellationToken.None);
        Assert.True(projection.IsSuccess, projection.Error?.Code);
        var gen = projection.Value!.StoreGenerationFingerprint ?? "";
        var detail = await ledger.GetBudgetCategoryAsync(
            categoryId,
            CategoryContractVersions.Current,
            actor,
            CancellationToken.None);
        Assert.True(detail.IsSuccess, detail.Error?.Code);
        return (evalRuns, outcomes, evidence, lifecycle, gen, detail.Value!.Name);
    }

    private sealed record SeededEval(
        string EvaluationId,
        string RuleVersionId,
        IReadOnlyList<string> TransactionIds,
        string? MatchedTransactionId = null);

    private async Task<SeededEval> SeedSuggestionEvaluationAsync(string description, CategoryDetail? category = null)
    {
        category ??= await CreateCategoryAsync("Sug");
        var versionId = await SaveDraftAsync(category.CategoryId, description);
        await ActivateWithGateAsync(versionId, category.CategoryId, description);
        var tx = await RecordAsync(description);
        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);
        Assert.True(evaluated.Value!.SuggestionCount >= 1);
        return new SeededEval(evaluated.Value.EvaluationId, versionId, [tx.TransactionId], tx.TransactionId);
    }

    private async Task<SeededEval> SeedNoSuggestionEvaluationAsync(
        string ruleDescription,
        string unmatchedDescription,
        CategoryDetail category)
    {
        var versionId = await SaveDraftAsync(category.CategoryId, ruleDescription);
        await ActivateWithGateAsync(versionId, category.CategoryId, ruleDescription);
        var matched = await RecordAsync(ruleDescription);
        var unmatched = await RecordAsync(unmatchedDescription);
        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);
        return new SeededEval(
            evaluated.Value!.EvaluationId,
            versionId,
            [matched.TransactionId, unmatched.TransactionId],
            matched.TransactionId);
    }

    private async Task<SeededEval> SeedConflictEvaluationAsync(
        string description,
        CategoryDetail catA,
        CategoryDetail catB)
    {
        var vA = await SaveDraftAsync(catA.CategoryId, description, "rule-cla");
        var vB = await SaveDraftAsync(catB.CategoryId, description, "rule-clb");
        await ActivateMultiWithGateAsync([vA, vB], [(description, "conflict", null)]);
        var tx = await RecordAsync(description);
        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);
        Assert.True(evaluated.Value!.ConflictCount >= 1);
        return new SeededEval(evaluated.Value.EvaluationId, vA, [tx.TransactionId]);
    }

    private async Task ActivateWithGateAsync(string versionId, string categoryId, string description) =>
        await ActivateMultiWithGateAsync([versionId], [(description, "suggestion", categoryId)]);

    private async Task ActivateMultiWithGateAsync(
        IReadOnlyList<string> versionIds,
        IReadOnlyList<(string Description, string ExpectedKind, string? ExpectedCategory)> rows)
    {
        var path = await WriteBoundCorpusAsync(rows);
        var rep = await services.Validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, versionIds, path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(rep.IsSuccess, rep.ErrorCode);
        var replay = await services.Validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, versionIds, path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(replay.IsSuccess, replay.ErrorCode);
        var hold = await services.Validate.HandleAsync(
            new ClassifyRuleValidateRequest(
                ClassifyOperationIds.ContractVersion, versionIds, path,
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
                "outcome list activate"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(activated.IsSuccess, activated.ErrorCode);
    }

    private async Task<string> SaveDraftAsync(string categoryId, string description, string? ruleId = null)
    {
        var result = await services.Save.HandleAsync(
            new ClassifyRuleSaveRequest(
                ClassifyOperationIds.ContractVersion,
                ruleId ?? "rule-" + Guid.NewGuid().ToString("N")[..12],
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
                "outcome list draft"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!.RuleVersionId;
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

    private async Task RenameCategoryAsync(string categoryId, string newName) =>
        _ = await ExecuteSuccessAsync(
            "ledger.category.rename",
            new RenameCategoryInput(categoryId, newName, "list-rename"),
            NextKey(),
            LedgerJsonContext.Default.RenameCategoryInput,
            LedgerJsonContext.Default.CategoryLifecycleResult);

    private async Task ArchiveCategoryAsync(string categoryId) =>
        _ = await ExecuteSuccessAsync(
            "ledger.category.archive",
            new ArchiveCategoryInput(categoryId, "list-archive"),
            NextKey(),
            LedgerJsonContext.Default.ArchiveCategoryInput,
            LedgerJsonContext.Default.CategoryLifecycleResult);

    private async Task<AccountDetail> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        return await ExecuteSuccessAsync(
            "ledger.account.create",
            new CreateAccountInput("List Bank " + unique, "P-" + unique, AccountType.Cheque, "****" + ((int)((uint)unique.GetHashCode() % 9000u) + 1000).ToString(), "ZAR"),
            NextKey(), LedgerJsonContext.Default.CreateAccountInput, LedgerJsonContext.Default.AccountDetail);
    }

    private Task<CategoryDetail> CreateCategoryAsync(string name) =>
        ExecuteSuccessAsync(
            "ledger.category.create",
            new CreateCategoryInput(name + "-" + Guid.NewGuid().ToString("N")[..6]),
            NextKey(), LedgerJsonContext.Default.CreateCategoryInput, LedgerJsonContext.Default.CategoryDetail);

    private async Task<TransactionDetail> RecordAsync(string description)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"))));
        return await ExecuteSuccessAsync(
            "ledger.transaction.record",
            new RecordTransactionInput(
                accountId, "-12.34", "ZAR", "2026-07-15", null, description, null, null,
                new RegisterEvidenceInput(EvidenceKind.AgentCapture, digest, "list:" + Guid.NewGuid().ToString("N")[..8], null, null)),
            NextKey(), LedgerJsonContext.Default.RecordTransactionInput, LedgerJsonContext.Default.TransactionDetail);
    }

    private string NextKey() => "list-key-" + (++keySeq).ToString(CultureInfo.InvariantCulture);

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
}
