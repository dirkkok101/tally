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
using Tally.Features.Classify.Apply.Preview;
using Tally.Features.Classify.Contract;
using Tally.Features.Classify.Evaluation.Outcome;
using Tally.Infrastructure.Classify.Storage.Apply;
using Tally.Infrastructure.Classify.Storage.Evaluation;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Classify.Acceptance;

/// <summary>
/// TASK-CLASSIFY-ERGONOMICS-BULK-PREVIEW-COMPOSITION / bd-wsjo —
/// Prove one outcome.list page supplies explicit selected_outcomes IDs for unchanged apply.preview
/// without outcome.get. Synthetic isolated roots only; no product contract changes.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassifyOperatorBatchPreviewTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-batch-preview-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("automation", "batch-preview", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private ClassifyEvaluationServices services = null!;
    private ListClassificationOutcomesQuery listQuery = null!;
    private PreviewClassificationApplyCommand preview = null!;
    private ClassificationApplyPreviewStore previewStore = null!;
    private string accountId = null!;
    private int keySeq;
    private int outcomeGetCalls;

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
        previewStore = new ClassificationApplyPreviewStore();
        preview = new PreviewClassificationApplyCommand(
            services.State.Store,
            services.EvaluationStore,
            previewStore,
            services.RuleSetStore,
            services.RuleStore,
            ledger,
            services.State.Idempotency);
        accountId = (await CreateAccountAsync()).AccountId;
        outcomeGetCalls = 0;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ── Happy path composition ───────────────────────────────────────────────

    [Fact]
    public async Task Outcome_list_page_supplies_selected_outcomes_without_outcome_get()
    {
        var seeded = await SeedSuggestionsAsync("batch shop", count: 3);
        var list = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", seeded.EvaluationId, 500, OutcomeKind: ClassifyOutcomeKind.Suggestion),
            actor,
            CancellationToken.None);
        Assert.True(list.IsSuccess, list.ErrorCode);
        Assert.True(list.Value!.Items.Count >= 3);
        Assert.Equal(0, outcomeGetCalls);

        // Explicit ordered subset from list page (request order preserved as given; auth reorders by tx).
        var orderedIds = list.Value.Items
            .OrderBy(i => i.Ordinal)
            .ThenBy(i => i.TransactionId, StringComparer.Ordinal)
            .Select(i => i.OutcomeId)
            .Take(2)
            .ToArray();

        var before = await SnapshotCategoryStatesAsync(seeded.TransactionIds);
        var result = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                seeded.EvaluationId,
                new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes, OutcomeIds: orderedIds)),
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(2, result.Value!.SelectedCount);
        Assert.Equal(2, result.Value.AssignableCount);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.PreviewId));
        Assert.Equal(0, outcomeGetCalls);
        Assert.Equal(before, await SnapshotCategoryStatesAsync(seeded.TransactionIds));
    }

    [Fact]
    public async Task Composition_preview_freezes_evaluation_and_ledger_evidence()
    {
        var seeded = await SeedSuggestionsAsync("freeze shop", count: 2);
        var ids = await ListSuggestionIdsFromOutcomeListAsync(seeded.EvaluationId);
        var result = await preview.HandleAsync(
            PreviewSelected(seeded.EvaluationId, ids),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var v = result.Value!;
        Assert.Equal(seeded.EvaluationId, v.EvaluationId);
        Assert.Equal(64, v.EvaluationFingerprint.Length);
        Assert.Equal(ClassifyContractMapper.SelectionModeSelectedOutcomes, v.SelectionMode);
        Assert.Equal(64, v.SelectionHash.Length);
        Assert.Equal(64, v.TargetCategoryFingerprint.Length);
        Assert.Equal(64, v.RuleAuthorityFingerprint.Length);
        Assert.Equal(64, v.StoreGenerationFingerprint.Length);
        Assert.Equal(64, v.CategoryLifecycleFingerprint.Length);
        Assert.False(string.IsNullOrWhiteSpace(v.LedgerContractVersion));
        Assert.False(string.IsNullOrWhiteSpace(v.ProjectionVersion));
        Assert.False(string.IsNullOrWhiteSpace(v.PreflightSnapshotId));
        Assert.False(string.IsNullOrWhiteSpace(v.ExpiresAt));
        Assert.Equal(v.PreflightExpiresAt, v.ExpiresAt);
        Assert.Equal(v.SelectedCount, v.SelectedTransactionIds.Count);
        Assert.All(v.ContributingRuleVersionIds, id => Assert.False(string.IsNullOrWhiteSpace(id)));
    }

    [Fact]
    public async Task Composition_idempotent_replay_returns_same_preview()
    {
        var seeded = await SeedSuggestionsAsync("idem composition", count: 2);
        var ids = await ListSuggestionIdsFromOutcomeListAsync(seeded.EvaluationId);
        var key = NextKey();
        var request = PreviewSelected(seeded.EvaluationId, ids);
        var first = await preview.HandleAsync(request, actor, key, CancellationToken.None);
        var second = await preview.HandleAsync(request, actor, key, CancellationToken.None);
        Assert.True(first.IsSuccess && second.IsSuccess);
        Assert.Equal(first.Value!.PreviewId, second.Value!.PreviewId);
        Assert.Equal(first.Value.SelectionHash, second.Value.SelectionHash);
    }

    [Fact]
    public async Task Composition_does_not_mutate_ledger_categories()
    {
        var seeded = await SeedSuggestionsAsync("nomut composition", count: 2);
        var ids = await ListSuggestionIdsFromOutcomeListAsync(seeded.EvaluationId);
        var before = await SnapshotCategoryStatesAsync(seeded.TransactionIds);
        var result = await preview.HandleAsync(
            PreviewSelected(seeded.EvaluationId, ids),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(before, await SnapshotCategoryStatesAsync(seeded.TransactionIds));
    }

    // ── Failures: typed errors, no receipt, no mutation ──────────────────────

    [Fact]
    public async Task Duplicate_ids_in_selection_do_not_create_extra_candidates()
    {
        var seeded = await SeedSuggestionsAsync("dup shop", count: 1);
        var ids = await ListSuggestionIdsFromOutcomeListAsync(seeded.EvaluationId);
        Assert.NotEmpty(ids);
        var one = ids[0];
        var duplicated = new[] { one, one, one };
        var result = await preview.HandleAsync(
            PreviewSelected(seeded.EvaluationId, duplicated),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(1, result.Value!.SelectedCount);
    }

    [Fact]
    public async Task Missing_outcome_id_fails_without_preview_receipt_or_ledger_mutation()
    {
        var seeded = await SeedSuggestionsAsync("missing id", count: 1);
        var ids = await ListSuggestionIdsFromOutcomeListAsync(seeded.EvaluationId);
        var before = await SnapshotCategoryStatesAsync(seeded.TransactionIds);
        var previewCountBefore = await CountPreviewsAsync();

        var result = await preview.HandleAsync(
            PreviewSelected(seeded.EvaluationId, [ids[0], "outcome-does-not-exist"]),
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.Equal(ClassifyErrors.SelectionInvalid, result.ErrorCode);
        Assert.Null(result.Value);
        Assert.Equal(previewCountBefore, await CountPreviewsAsync());
        Assert.Equal(before, await SnapshotCategoryStatesAsync(seeded.TransactionIds));
    }

    [Fact]
    public async Task Only_no_suggestion_selection_fails_without_receipt()
    {
        var category = await CreateCategoryAsync("NoSug");
        var versionId = await SaveDraftAsync(category.CategoryId, "matched-only");
        await ActivateWithGateAsync(versionId, category.CategoryId, "matched-only", broadApply: false);
        _ = await RecordAsync("matched-only");
        var unmatched = await RecordAsync("unmatched-batch");
        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);

        var list = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest(
                "1.0",
                evaluated.Value!.EvaluationId,
                500,
                OutcomeKind: ClassifyOutcomeKind.NoSuggestion),
            actor,
            CancellationToken.None);
        Assert.True(list.IsSuccess, list.ErrorCode);
        var noSugIds = list.Value!.Items.Select(i => i.OutcomeId).ToArray();
        Assert.NotEmpty(noSugIds);

        var previewCountBefore = await CountPreviewsAsync();
        var before = await SnapshotCategoryStatesAsync([unmatched.TransactionId]);
        var result = await preview.HandleAsync(
            PreviewSelected(evaluated.Value.EvaluationId, noSugIds),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.SelectionInvalid, result.ErrorCode);
        Assert.Null(result.Value);
        Assert.Equal(previewCountBefore, await CountPreviewsAsync());
        Assert.Equal(before, await SnapshotCategoryStatesAsync([unmatched.TransactionId]));
    }

    [Fact]
    public async Task Only_conflict_selection_fails_without_receipt()
    {
        var catA = await CreateCategoryAsync("CflA");
        var catB = await CreateCategoryAsync("CflB");
        var vA = await SaveDraftAsync(catA.CategoryId, "clash-token", "rule-ca");
        var vB = await SaveDraftAsync(catB.CategoryId, "clash-token", "rule-cb");
        await ActivateMultiAsync([vA, vB], [("clash-token", "conflict", null)]);
        var tx = await RecordAsync("clash-token");
        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);
        Assert.True(evaluated.Value!.ConflictCount >= 1);

        var list = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest(
                "1.0",
                evaluated.Value.EvaluationId,
                500,
                OutcomeKind: ClassifyOutcomeKind.Conflict),
            actor,
            CancellationToken.None);
        Assert.True(list.IsSuccess, list.ErrorCode);
        var conflictIds = list.Value!.Items.Select(i => i.OutcomeId).ToArray();
        Assert.NotEmpty(conflictIds);

        var previewCountBefore = await CountPreviewsAsync();
        var before = await SnapshotCategoryStatesAsync([tx.TransactionId]);
        var result = await preview.HandleAsync(
            PreviewSelected(evaluated.Value.EvaluationId, conflictIds),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.SelectionInvalid, result.ErrorCode);
        Assert.Null(result.Value);
        Assert.Equal(previewCountBefore, await CountPreviewsAsync());
        Assert.Equal(before, await SnapshotCategoryStatesAsync([tx.TransactionId]));
    }

    [Fact]
    public async Task Stale_lifecycle_after_list_fails_preview_without_receipt()
    {
        var seeded = await SeedSuggestionsAsync("stale batch", count: 1);
        var ids = await ListSuggestionIdsFromOutcomeListAsync(seeded.EvaluationId);
        await VoidTransactionAsync(seeded.TransactionIds[0]);

        var previewCountBefore = await CountPreviewsAsync();
        var result = await preview.HandleAsync(
            PreviewSelected(seeded.EvaluationId, ids),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.True(
            result.ErrorCode is ClassifyErrors.Stale or ClassifyErrors.SelectionInvalid or ClassifyErrors.LedgerUnavailable,
            result.ErrorCode);
        Assert.Null(result.Value);
        Assert.Equal(previewCountBefore, await CountPreviewsAsync());
    }

    [Fact]
    public async Task Archived_target_category_fails_without_receipt()
    {
        var category = await CreateCategoryAsync("ArchBatch");
        var versionId = await SaveDraftAsync(category.CategoryId, "arch batch");
        await ActivateWithGateAsync(versionId, category.CategoryId, "arch batch", broadApply: false);
        var tx = await RecordAsync("arch batch");
        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);

        var list = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest(
                "1.0", evaluated.Value!.EvaluationId, 500, OutcomeKind: ClassifyOutcomeKind.Suggestion),
            actor,
            CancellationToken.None);
        // After archive, list may fail closed stale — archive after list IDs captured.
        Assert.True(list.IsSuccess, list.ErrorCode);
        var ids = list.Value!.Items.Select(i => i.OutcomeId).ToArray();
        Assert.NotEmpty(ids);

        await ArchiveCategoryAsync(category.CategoryId);

        var previewCountBefore = await CountPreviewsAsync();
        var before = await SnapshotCategoryStatesAsync([tx.TransactionId]);
        var result = await preview.HandleAsync(
            PreviewSelected(evaluated.Value.EvaluationId, ids),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.True(
            result.ErrorCode is ClassifyErrors.Stale or ClassifyErrors.SelectionInvalid or ClassifyErrors.Lifecycle,
            result.ErrorCode);
        Assert.Null(result.Value);
        Assert.Equal(previewCountBefore, await CountPreviewsAsync());
        Assert.Equal(before, await SnapshotCategoryStatesAsync([tx.TransactionId]));
    }

    [Fact]
    public async Task Two_hundred_one_candidates_hit_resource_limit_without_receipt()
    {
        // MaxApplyPreflightIds = 200; 201 authorized suggestions → ResourceLimit.
        var category = await CreateCategoryAsync("Bound201");
        const string phrase = "bound two hundred one";
        var versionId = await SaveDraftAsync(category.CategoryId, phrase);
        await ActivateWithGateAsync(versionId, category.CategoryId, phrase, broadApply: false);
        var txIds = new List<string>(201);
        for (var i = 0; i < 201; i++)
        {
            txIds.Add((await RecordAsync(phrase)).TransactionId);
        }

        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);

        var list = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest(
                "1.0", evaluated.Value!.EvaluationId, 500, OutcomeKind: ClassifyOutcomeKind.Suggestion),
            actor,
            CancellationToken.None);
        Assert.True(list.IsSuccess, list.ErrorCode);
        var ids = list.Value!.Items.Select(i => i.OutcomeId).ToArray();
        Assert.True(ids.Length >= 201, "need >= 201 suggestions for preflight bound");

        var take = ids.Take(201).ToArray();
        var previewCountBefore = await CountPreviewsAsync();
        var before = await SnapshotCategoryStatesAsync(txIds.Take(5).ToArray());
        var result = await preview.HandleAsync(
            PreviewSelected(evaluated.Value.EvaluationId, take),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.ResourceLimit, result.ErrorCode);
        Assert.Null(result.Value);
        Assert.Equal(previewCountBefore, await CountPreviewsAsync());
        Assert.Equal(before, await SnapshotCategoryStatesAsync(txIds.Take(5).ToArray()));
    }

    [Fact]
    public async Task Broad_apply_false_rejects_exact_rule_authority()
    {
        var seeded = await SeedSuggestionsAsync("narrow exact", count: 1, broadApply: false);
        var before = await SnapshotCategoryStatesAsync(seeded.TransactionIds);
        var previewCountBefore = await CountPreviewsAsync();
        var result = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                seeded.EvaluationId,
                new ClassifyApplySelection(ClassifyApplySelectionMode.ExactRule, RuleVersionId: seeded.RuleVersionId)),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.Lifecycle, result.ErrorCode);
        Assert.Null(result.Value);
        Assert.Equal(previewCountBefore, await CountPreviewsAsync());
        Assert.Equal(before, await SnapshotCategoryStatesAsync(seeded.TransactionIds));
    }

    [Fact]
    public async Task Omitted_selection_is_invalid()
    {
        var seeded = await SeedSuggestionsAsync("omit", count: 1);
        var result = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                seeded.EvaluationId,
                new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes)),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.SelectionInvalid, result.ErrorCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Empty_outcome_ids_is_invalid()
    {
        var seeded = await SeedSuggestionsAsync("empty ids", count: 1);
        var result = await preview.HandleAsync(
            PreviewSelected(seeded.EvaluationId, Array.Empty<string>()),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.SelectionInvalid, result.ErrorCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Mixed_mode_selection_is_invalid()
    {
        var seeded = await SeedSuggestionsAsync("mixed mode", count: 1);
        var ids = await ListSuggestionIdsFromOutcomeListAsync(seeded.EvaluationId);
        var result = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                seeded.EvaluationId,
                new ClassifyApplySelection(
                    ClassifyApplySelectionMode.SelectedOutcomes,
                    OutcomeIds: ids,
                    RuleVersionId: seeded.RuleVersionId)),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.SelectionInvalid, result.ErrorCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Request_shape_has_no_all_cursor_or_filter_authority()
    {
        // Composition contract: selection is only explicit OutcomeIds / ExactRule / ExplicitCorrections.
        var props = typeof(ClassifyApplySelection).GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(nameof(ClassifyApplySelection.OutcomeIds), props);
        Assert.Contains(nameof(ClassifyApplySelection.Mode), props);
        Assert.DoesNotContain("Cursor", props);
        Assert.DoesNotContain("Continuation", props);
        Assert.DoesNotContain("Filter", props);
        Assert.DoesNotContain("All", props);
        Assert.DoesNotContain("PageSize", props);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Composition_privacy_excludes_description_canaries()
    {
        const string canary = "CANARY_BATCH_PRIVATE_DESC_xyz";
        var seeded = await SeedSuggestionsAsync(canary, count: 1);
        var ids = await ListSuggestionIdsFromOutcomeListAsync(seeded.EvaluationId);
        var list = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", seeded.EvaluationId, 50),
            actor,
            CancellationToken.None);
        Assert.True(list.IsSuccess, list.ErrorCode);
        var listJson = JsonSerializer.Serialize(list.Value, ClassifyJsonContext.Default.ClassifyOutcomeListResult);
        Assert.DoesNotContain(canary, listJson, StringComparison.OrdinalIgnoreCase);

        var result = await preview.HandleAsync(
            PreviewSelected(seeded.EvaluationId, ids),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var previewJson = JsonSerializer.Serialize(result.Value, ClassifyJsonContext.Default.ClassifyApplyPreviewResult);
        Assert.DoesNotContain(canary, previewJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceDescription", previewJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Idempotency_conflict_on_different_list_subset()
    {
        var seeded = await SeedSuggestionsAsync("idem conflict", count: 2);
        var ids = await ListSuggestionIdsFromOutcomeListAsync(seeded.EvaluationId);
        Assert.True(ids.Count >= 2);
        var key = NextKey();
        var first = await preview.HandleAsync(
            PreviewSelected(seeded.EvaluationId, [ids[0]]),
            actor,
            key,
            CancellationToken.None);
        Assert.True(first.IsSuccess, first.ErrorCode);
        var second = await preview.HandleAsync(
            PreviewSelected(seeded.EvaluationId, [ids[1]]),
            actor,
            key,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.IdempotencyConflict, second.ErrorCode);
        Assert.Null(second.Value);
    }

    [Fact]
    public async Task List_ordered_ids_compose_to_deterministic_preview_transactions()
    {
        var seeded = await SeedSuggestionsAsync("order compose", count: 3);
        var list = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest(
                "1.0", seeded.EvaluationId, 500, OutcomeKind: ClassifyOutcomeKind.Suggestion),
            actor,
            CancellationToken.None);
        Assert.True(list.IsSuccess, list.ErrorCode);
        var forward = list.Value!.Items.Select(i => i.OutcomeId).ToArray();
        var reverse = forward.Reverse().ToArray();

        var a = await preview.HandleAsync(PreviewSelected(seeded.EvaluationId, forward), actor, NextKey(), CancellationToken.None);
        var b = await preview.HandleAsync(PreviewSelected(seeded.EvaluationId, reverse), actor, NextKey(), CancellationToken.None);
        Assert.True(a.IsSuccess && b.IsSuccess);
        Assert.Equal(a.Value!.SelectedTransactionIds.ToArray(), b.Value!.SelectedTransactionIds.ToArray());
        Assert.Equal(
            a.Value.SelectedTransactionIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            a.Value.SelectedTransactionIds.ToArray());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed record SeededBatch(
        string EvaluationId,
        string RuleVersionId,
        IReadOnlyList<string> TransactionIds);

    private static ClassifyApplyPreviewRequest PreviewSelected(string evaluationId, IReadOnlyList<string> outcomeIds) =>
        new(
            ClassifyOperationIds.ContractVersion,
            evaluationId,
            new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes, OutcomeIds: outcomeIds));

    private async Task<IReadOnlyList<string>> ListSuggestionIdsFromOutcomeListAsync(string evaluationId)
    {
        var list = await listQuery.HandleAsync(
            new ClassifyOutcomeListRequest("1.0", evaluationId, 500, OutcomeKind: ClassifyOutcomeKind.Suggestion),
            actor,
            CancellationToken.None);
        Assert.True(list.IsSuccess, list.ErrorCode);
        return list.Value!.Items.Select(i => i.OutcomeId).ToArray();
    }

    private async Task<SeededBatch> SeedSuggestionsAsync(string description, int count, bool broadApply = false)
    {
        var category = await CreateCategoryAsync("Batch");
        var versionId = await SaveDraftAsync(category.CategoryId, description);
        await ActivateWithGateAsync(versionId, category.CategoryId, description, broadApply);
        var txIds = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            txIds.Add((await RecordAsync(description)).TransactionId);
        }

        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);
        Assert.True(evaluated.Value!.SuggestionCount >= count);
        return new SeededBatch(evaluated.Value.EvaluationId, versionId, txIds);
    }

    private async Task ActivateMultiAsync(
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
                "batch conflict activate"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(activated.IsSuccess, activated.ErrorCode);
    }

    private async Task ActivateWithGateAsync(string versionId, string categoryId, string description, bool broadApply)
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
                broadApply,
                "batch preview activate"),
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
                "batch preview draft"),
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

    private async Task<long> CountPreviewsAsync()
    {
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        return await previewStore.CountPreviewsAsync(connection, null, CancellationToken.None);
    }

    private async Task<Dictionary<string, (string? CategoryId, string? AllocationId, string Lifecycle)>> SnapshotCategoryStatesAsync(
        IReadOnlyList<string> transactionIds)
    {
        var ordered = transactionIds.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var page = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.ApplyPreflight,
            ActualsContractVersions.Current,
            actor,
            CancellationToken.None,
            transactionIds: ordered);
        Assert.True(page.IsSuccess, page.Error?.Code);
        var map = new Dictionary<string, (string?, string?, string)>(StringComparer.Ordinal);
        foreach (var id in ordered)
        {
            var item = page.Value!.ClassificationItems?
                .FirstOrDefault(i => string.Equals(i.TransactionId, id, StringComparison.Ordinal));
            if (item is null)
            {
                map[id] = (null, null, "missing");
                continue;
            }

            map[id] = (
                item.CurrentCategoryId,
                item.CurrentAllocationId,
                ClassifyContractMapper.ComputeItemLifecycleFingerprint(item));
        }

        return map;
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
                new VoidTransactionInput(transactionId, "batch-void"),
                TransactionCorrectionJsonContext.Default.VoidTransactionInput),
            NextKey());
        var json = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var args = descriptor.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Concat(["--input", "-"]).ToArray();
        _ = await process.RunAsync(args, json, CancellationToken.None);
    }

    private async Task ArchiveCategoryAsync(string categoryId) =>
        _ = await ExecuteSuccessAsync(
            "ledger.category.archive",
            new ArchiveCategoryInput(categoryId, "batch-archive"),
            NextKey(),
            LedgerJsonContext.Default.ArchiveCategoryInput,
            LedgerJsonContext.Default.CategoryLifecycleResult);

    private async Task<AccountDetail> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        return await ExecuteSuccessAsync(
            "ledger.account.create",
            new CreateAccountInput("Batch Bank " + unique, "P-" + unique, AccountType.Cheque, "****" + ((int)((uint)unique.GetHashCode() % 9000u) + 1000).ToString(), "ZAR"),
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
                new RegisterEvidenceInput(EvidenceKind.AgentCapture, digest, "batch:" + Guid.NewGuid().ToString("N")[..8], null, null)),
            NextKey(), LedgerJsonContext.Default.RecordTransactionInput, LedgerJsonContext.Default.TransactionDetail);
    }

    private string NextKey() => "batch-key-" + (++keySeq).ToString(CultureInfo.InvariantCulture);

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
