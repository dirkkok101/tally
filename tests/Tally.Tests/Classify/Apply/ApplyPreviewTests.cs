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
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Apply;
using Tally.Infrastructure.Classify.Storage.Evaluation;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Classify.Apply;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-APPLY-PREVIEW / bd-gv0z — preview command contract, preflight, expiry, no Ledger mutation.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ApplyPreviewTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-apply-preview-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("automation", "apply-preview", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private ClassifyEvaluationServices services = null!;
    private PreviewClassificationApplyCommand preview = null!;
    private ClassificationApplyPreviewStore previewStore = null!;
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
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ── Guard rails ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Preview_requires_actor()
    {
        var result = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                "e",
                new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes, OutcomeIds: ["o"])),
            null,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.ActorRequired, result.ErrorCode);
    }

    [Fact]
    public async Task Preview_requires_idempotency_key()
    {
        var result = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                "e",
                new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes, OutcomeIds: ["o"])),
            actor,
            null,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.IdempotencyRequired, result.ErrorCode);
    }

    [Fact]
    public async Task Preview_rejects_unsupported_version()
    {
        var result = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                "9.9",
                "e",
                new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes, OutcomeIds: ["o"])),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.UnsupportedVersion, result.ErrorCode);
    }

    [Fact]
    public async Task Preview_rejects_unknown_evaluation()
    {
        var result = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                "missing-eval",
                new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes, OutcomeIds: ["o"])),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.EvaluationNotFound, result.ErrorCode);
    }

    [Fact]
    public async Task Preview_rejects_mixed_mode_selection()
    {
        var result = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                "eval",
                new ClassifyApplySelection(
                    ClassifyApplySelectionMode.SelectedOutcomes,
                    OutcomeIds: ["o"],
                    RuleVersionId: "rv")),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.SelectionInvalid, result.ErrorCode);
    }

    // ── Selected outcomes happy path ────────────────────────────────────────

    [Fact]
    public async Task Selected_outcomes_preview_persists_assignable_items_without_ledger_mutation()
    {
        var seeded = await SeedSuggestionAsync("preview shop");
        var outcomes = await ListOutcomesAsync(seeded.EvaluationId);
        var suggestionIds = outcomes
            .Where(o => o.OutcomeType == "suggestion")
            .Select(o => o.OutcomeId)
            .ToArray();
        Assert.NotEmpty(suggestionIds);

        var before = await SnapshotCategoryStatesAsync(seeded.TransactionIds);

        var result = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                seeded.EvaluationId,
                new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes, OutcomeIds: suggestionIds)),
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(ClassifyOperationIds.ContractVersion, result.Value!.ContractVersion);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.PreviewId));
        Assert.Equal(seeded.EvaluationId, result.Value.EvaluationId);
        Assert.True(result.Value.SelectedCount >= 1);
        Assert.True(result.Value.AssignableCount >= 1);
        Assert.Equal(0, result.Value.CorrectableCount);
        Assert.Equal(64, result.Value.SelectionHash.Length);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.ExpiresAt));
        // Complete public disclosure (FR-CLASSIFY-APPLY-AUTHORIZATION) — not private rows only.
        Assert.Equal(64, result.Value.EvaluationFingerprint.Length);
        Assert.Equal(ClassifyContractMapper.SelectionModeSelectedOutcomes, result.Value.SelectionMode);
        Assert.Equal(64, result.Value.TargetCategoryFingerprint.Length);
        Assert.Equal(64, result.Value.RuleAuthorityFingerprint.Length);
        Assert.Equal(result.Value.SelectedCount, result.Value.SelectedTransactionIds.Count);
        Assert.Equal(result.Value.SelectedCount, result.Value.TargetCategoryIds.Count);
        Assert.Contains(seeded.TransactionIds[0], result.Value.SelectedTransactionIds);
        Assert.All(result.Value.ContributingRuleVersionIds, id => Assert.False(string.IsNullOrWhiteSpace(id)));
        Assert.Equal(
            result.Value.ContributingRuleVersionIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            result.Value.ContributingRuleVersionIds.ToArray());
        Assert.False(string.IsNullOrWhiteSpace(result.Value.LedgerContractVersion));
        Assert.False(string.IsNullOrWhiteSpace(result.Value.ProjectionVersion));
        Assert.Equal(64, result.Value.StoreGenerationFingerprint.Length);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.PreflightSnapshotId));
        Assert.False(string.IsNullOrWhiteSpace(result.Value.PreflightExpiresAt));
        Assert.Equal(64, result.Value.CategoryLifecycleFingerprint.Length);
        Assert.True(result.Value.ExclusionCount >= 0);
        Assert.True(result.Value.NoSuggestionCount >= 0);
        Assert.True(result.Value.ConflictCount >= 0);

        var after = await SnapshotCategoryStatesAsync(seeded.TransactionIds);
        Assert.Equal(before, after);

        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        var row = await previewStore.GetPreviewAsync(connection, null, result.Value.PreviewId, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal(result.Value.SelectedCount, row!.SelectedCount);
        Assert.Equal(64, row.EvaluationFingerprint.Length);
        Assert.Equal(64, row.SelectionHash.Length);
        Assert.DoesNotContain("preview shop", row.SelectionMode, StringComparison.OrdinalIgnoreCase);

        var items = await previewStore.ListItemsAsync(connection, null, result.Value.PreviewId, CancellationToken.None);
        Assert.Equal(result.Value.SelectedCount, items.Count);
        Assert.All(items, i =>
        {
            Assert.Equal("assign", i.Mode);
            Assert.False(string.IsNullOrWhiteSpace(i.ExpectedTransactionRevision));
            Assert.False(string.IsNullOrWhiteSpace(i.ExpectedRelationshipRevision));
            Assert.False(string.IsNullOrWhiteSpace(i.ExpectedAllocationRevision));
            Assert.Null(i.CorrectionReason);
            // Privacy: no description field exists on the row type.
        });
        Assert.Equal(
            items.Select(i => i.TransactionId).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            items.Select(i => i.TransactionId).ToArray());
    }

    [Fact]
    public async Task Selected_outcomes_excludes_no_suggestion_and_conflict_without_mutating_ledger()
    {
        var category = await CreateCategoryAsync("Mix");
        var versionId = await SaveDraftAsync(category.CategoryId, "matched-token");
        await ActivateWithGateAsync(versionId, category.CategoryId, "matched-token", broadApply: false);
        var matched = await RecordAsync("matched-token");
        var unmatched = await RecordAsync("totally-different");
        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);

        var outcomes = await ListOutcomesAsync(evaluated.Value!.EvaluationId);
        var allIds = outcomes.Select(o => o.OutcomeId).ToArray();
        var before = await SnapshotCategoryStatesAsync([matched.TransactionId, unmatched.TransactionId]);

        var result = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                evaluated.Value.EvaluationId,
                new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes, OutcomeIds: allIds)),
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.True(result.Value!.SelectedCount >= 1);
        Assert.True(result.Value.SelectedCount < allIds.Length || evaluated.Value.NoSuggestionCount == 0);

        var after = await SnapshotCategoryStatesAsync([matched.TransactionId, unmatched.TransactionId]);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Selected_outcomes_idempotent_replay_returns_same_preview()
    {
        var seeded = await SeedSuggestionAsync("idem shop");
        var outcomes = await ListOutcomesAsync(seeded.EvaluationId);
        var suggestionIds = outcomes.Where(o => o.OutcomeType == "suggestion").Select(o => o.OutcomeId).ToArray();
        var key = NextKey();
        var request = new ClassifyApplyPreviewRequest(
            ClassifyOperationIds.ContractVersion,
            seeded.EvaluationId,
            new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes, OutcomeIds: suggestionIds));

        var first = await preview.HandleAsync(request, actor, key, CancellationToken.None);
        Assert.True(first.IsSuccess, first.ErrorCode);
        var second = await preview.HandleAsync(request, actor, key, CancellationToken.None);
        Assert.True(second.IsSuccess, second.ErrorCode);
        Assert.Equal(first.Value!.PreviewId, second.Value!.PreviewId);
        Assert.Equal(first.Value.SelectionHash, second.Value.SelectionHash);

        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        Assert.Equal(1, await previewStore.CountPreviewsAsync(connection, null, CancellationToken.None));
    }

    [Fact]
    public async Task Selected_outcomes_idempotency_conflict_on_different_selection()
    {
        var seeded = await SeedSuggestionAsync("conflict shop");
        var outcomes = await ListOutcomesAsync(seeded.EvaluationId);
        var suggestionIds = outcomes.Where(o => o.OutcomeType == "suggestion").Select(o => o.OutcomeId).ToArray();
        Assert.NotEmpty(suggestionIds);
        var key = NextKey();

        var first = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                seeded.EvaluationId,
                new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes, OutcomeIds: suggestionIds)),
            actor, key, CancellationToken.None);
        Assert.True(first.IsSuccess, first.ErrorCode);

        var second = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                seeded.EvaluationId,
                new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes, OutcomeIds: [suggestionIds[0] + "-other"])),
            actor, key, CancellationToken.None);
        Assert.Equal(ClassifyErrors.IdempotencyConflict, second.ErrorCode);
    }

    // ── Exact rule / broad authority ────────────────────────────────────────

    [Fact]
    public async Task Exact_rule_without_broad_authority_is_rejected_before_ledger_mutation()
    {
        var seeded = await SeedSuggestionAsync("narrow shop", broadApply: false);
        var before = await SnapshotCategoryStatesAsync(seeded.TransactionIds);

        var result = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                seeded.EvaluationId,
                new ClassifyApplySelection(ClassifyApplySelectionMode.ExactRule, RuleVersionId: seeded.RuleVersionId)),
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.Equal(ClassifyErrors.Lifecycle, result.ErrorCode);
        var after = await SnapshotCategoryStatesAsync(seeded.TransactionIds);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Exact_rule_with_broad_authority_authorizes_only_that_rules_assignments()
    {
        var seeded = await SeedSuggestionAsync("broad shop", broadApply: true);
        var before = await SnapshotCategoryStatesAsync(seeded.TransactionIds);

        var result = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                seeded.EvaluationId,
                new ClassifyApplySelection(ClassifyApplySelectionMode.ExactRule, RuleVersionId: seeded.RuleVersionId)),
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.True(result.Value!.SelectedCount >= 1);
        Assert.True(result.Value.AssignableCount >= 1);
        Assert.Equal(0, result.Value.CorrectableCount);

        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        var items = await previewStore.ListItemsAsync(connection, null, result.Value.PreviewId, CancellationToken.None);
        Assert.All(items, i =>
        {
            Assert.Equal("assign", i.Mode);
            Assert.Equal(seeded.RuleVersionId, i.RuleVersionId);
        });

        var after = await SnapshotCategoryStatesAsync(seeded.TransactionIds);
        Assert.Equal(before, after);
    }

    // ── Explicit corrections ────────────────────────────────────────────────

    [Fact]
    public async Task Explicit_correction_preview_uses_correctable_preflight_without_ledger_mutation()
    {
        var catA = await CreateCategoryAsync("CorrA");
        var catB = await CreateCategoryAsync("CorrB");
        var versionId = await SaveDraftAsync(catA.CategoryId, "corr-token");
        await ActivateWithGateAsync(versionId, catA.CategoryId, "corr-token", broadApply: false);
        var tx = await RecordAsync("corr-token");
        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);

        // Categorize so preflight reports correctable.
        await AssignCategoryAsync(tx.TransactionId, catA.CategoryId, "seed assign");
        var outcomes = await ListOutcomesAsync(evaluated.Value!.EvaluationId);
        var outcome = outcomes.Single(o => o.TransactionId == tx.TransactionId);

        var before = await SnapshotCategoryStatesAsync([tx.TransactionId]);
        var result = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                evaluated.Value.EvaluationId,
                new ClassifyApplySelection(
                    ClassifyApplySelectionMode.ExplicitCorrections,
                    CorrectionItems:
                    [
                        new ClassifyExplicitCorrectionItem(
                            tx.TransactionId,
                            outcome.OutcomeId,
                            catA.CategoryId,
                            catB.CategoryId,
                            "owner correction reason")
                    ])),
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(1, result.Value!.SelectedCount);
        Assert.Equal(0, result.Value.AssignableCount);
        Assert.Equal(1, result.Value.CorrectableCount);

        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        var items = await previewStore.ListItemsAsync(connection, null, result.Value.PreviewId, CancellationToken.None);
        Assert.Single(items);
        Assert.Equal("correct", items[0].Mode);
        Assert.Equal(catB.CategoryId, items[0].CategoryId);
        Assert.Equal(catA.CategoryId, items[0].ExpectedCurrentCategoryId);
        Assert.False(string.IsNullOrWhiteSpace(items[0].ExpectedActiveAllocationId));
        Assert.Equal("owner correction reason", items[0].CorrectionReason);
        Assert.Null(items[0].RuleVersionId);

        var after = await SnapshotCategoryStatesAsync([tx.TransactionId]);
        Assert.Equal(before, after);
        Assert.Equal(catA.CategoryId, after[tx.TransactionId].CategoryId);
        Assert.Equal(before[tx.TransactionId].AllocationId, after[tx.TransactionId].AllocationId);
    }

    [Fact]
    public async Task Explicit_correction_rejects_broad_mix()
    {
        var result = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                "eval",
                new ClassifyApplySelection(
                    ClassifyApplySelectionMode.ExactRule,
                    RuleVersionId: "rv",
                    CorrectionItems:
                    [
                        new ClassifyExplicitCorrectionItem("tx", "out", "a", "b", "r")
                    ])),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.SelectionInvalid, result.ErrorCode);
    }

    // ── Stale / unauthorized / cancellation ─────────────────────────────────

    [Fact]
    public async Task Preview_rejects_stale_when_item_lifecycle_drifts_for_assign()
    {
        var seeded = await SeedSuggestionAsync("stale-life");
        var outcomes = await ListOutcomesAsync(seeded.EvaluationId);
        var suggestion = outcomes.First(o => o.OutcomeType == "suggestion");

        // Voiding the transaction changes public lifecycle → assign preview must fail closed.
        await VoidTransactionAsync(suggestion.TransactionId);

        var result = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                seeded.EvaluationId,
                new ClassifyApplySelection(
                    ClassifyApplySelectionMode.SelectedOutcomes,
                    OutcomeIds: [suggestion.OutcomeId])),
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.True(
            result.ErrorCode is ClassifyErrors.Stale or ClassifyErrors.SelectionInvalid or ClassifyErrors.LedgerUnavailable,
            result.ErrorCode);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Preview_rejects_assignment_when_target_category_was_reactivated_after_evaluation()
    {
        var seeded = await SeedSuggestionAsync("reactivated-target");
        var outcomes = await ListOutcomesAsync(seeded.EvaluationId);
        var suggestion = outcomes.First(o => o.OutcomeType == "suggestion");

        await ArchiveCategoryAsync(suggestion.CategoryId!);
        await ReactivateCategoryAsync(suggestion.CategoryId!);

        var result = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                seeded.EvaluationId,
                new ClassifyApplySelection(
                    ClassifyApplySelectionMode.SelectedOutcomes,
                    OutcomeIds: [suggestion.OutcomeId])),
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.Equal(ClassifyErrors.Stale, result.ErrorCode);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Preview_honours_cancellation()
    {
        var seeded = await SeedSuggestionAsync("cancel shop");
        var outcomes = await ListOutcomesAsync(seeded.EvaluationId);
        var ids = outcomes.Where(o => o.OutcomeType == "suggestion").Select(o => o.OutcomeId).ToArray();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                seeded.EvaluationId,
                new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes, OutcomeIds: ids)),
            actor,
            NextKey(),
            cts.Token);

        Assert.True(
            result.ErrorCode is ClassifyErrors.Unexpected or ClassifyErrors.ResourceLimit,
            result.ErrorCode);
    }

    [Fact]
    public async Task Preview_store_counts_and_disclosure_fields()
    {
        var seeded = await SeedSuggestionAsync("disclose shop");
        var outcomes = await ListOutcomesAsync(seeded.EvaluationId);
        var ids = outcomes.Where(o => o.OutcomeType == "suggestion").Select(o => o.OutcomeId).ToArray();
        var result = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                seeded.EvaluationId,
                new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes, OutcomeIds: ids)),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        var row = await previewStore.GetPreviewAsync(connection, null, result.Value!.PreviewId, CancellationToken.None);
        Assert.NotNull(row);
        Assert.True(row!.NoSuggestionCount >= 0);
        Assert.True(row.ConflictCount >= 0);
        Assert.True(row.ExclusionCount >= 0);
        Assert.Equal("selected_outcomes", row.SelectionMode);
        Assert.False(string.IsNullOrWhiteSpace(row.PreflightSnapshotId));
        Assert.Equal(row.ExpiresAt, row.PreflightExpiresAt);
    }

    [Fact]
    public async Task Preview_result_expires_at_is_parseable_utc()
    {
        var seeded = await SeedSuggestionAsync("expiry shop");
        var outcomes = await ListOutcomesAsync(seeded.EvaluationId);
        var ids = outcomes.Where(o => o.OutcomeType == "suggestion").Select(o => o.OutcomeId).ToArray();
        var result = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                seeded.EvaluationId,
                new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes, OutcomeIds: ids)),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.True(DateTimeOffset.TryParse(
            result.Value!.ExpiresAt,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var expires));
        Assert.True(expires > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void Mapper_to_apply_preview_result_discloses_complete_public_surface()
    {
        var preview = new ClassifyApplyPreviewRow(
            PreviewId: "prev-1",
            OperationIdempotencyKey: "key-1",
            EvaluationId: "eval-1",
            EvaluationFingerprint: new string('a', 64),
            SelectionMode: ClassifyContractMapper.SelectionModeSelectedOutcomes,
            SelectionHash: new string('b', 64),
            LedgerContractVersion: ActualsContractVersions.Current,
            ProjectionVersion: ClassificationProjectionVersions.ClassificationV1,
            StoreGenerationFingerprint: new string('c', 64),
            PreflightSnapshotId: "snap-1",
            PreflightExpiresAt: "2026-08-01T12:00:00.0000000Z",
            CategoryLifecycleFingerprint: new string('d', 64),
            TargetCategoryFingerprint: new string('e', 64),
            RuleAuthorityFingerprint: new string('f', 64),
            ExpiresAt: "2026-08-01T12:00:00.0000000Z",
            SelectedCount: 2,
            ExclusionCount: 3,
            NoSuggestionCount: 1,
            ConflictCount: 1,
            Actor: "automation:preview",
            CreatedAt: "2026-08-01T11:00:00.0000000Z");
        var items = new[]
        {
            new ClassifyApplyPreviewItemRow(
                "prev-1", 1, "out-b", "tx-b", "assign", "cat-2", "rv-2",
                null, null, "tr-b", "rr-b", "ar-b", null),
            new ClassifyApplyPreviewItemRow(
                "prev-1", 0, "out-a", "tx-a", "assign", "cat-1", "rv-1",
                null, null, "tr-a", "rr-a", "ar-a", null)
        };

        var result = ClassifyContractMapper.ToApplyPreviewResult(preview, items, assignableCount: 2, correctableCount: 0);

        Assert.Equal("prev-1", result.PreviewId);
        Assert.Equal("eval-1", result.EvaluationId);
        Assert.Equal(new string('a', 64), result.EvaluationFingerprint);
        Assert.Equal(ClassifyContractMapper.SelectionModeSelectedOutcomes, result.SelectionMode);
        Assert.Equal(new string('b', 64), result.SelectionHash);
        Assert.Equal(new string('e', 64), result.TargetCategoryFingerprint);
        Assert.Equal(new string('f', 64), result.RuleAuthorityFingerprint);
        Assert.Equal(["tx-a", "tx-b"], result.SelectedTransactionIds.ToArray());
        Assert.Equal(["cat-1", "cat-2"], result.TargetCategoryIds.ToArray());
        Assert.Equal(["rv-1", "rv-2"], result.ContributingRuleVersionIds.ToArray());
        Assert.Equal(2, result.SelectedCount);
        Assert.Equal(2, result.AssignableCount);
        Assert.Equal(0, result.CorrectableCount);
        Assert.Equal(3, result.ExclusionCount);
        Assert.Equal(1, result.NoSuggestionCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.Equal(ActualsContractVersions.Current, result.LedgerContractVersion);
        Assert.Equal(ClassificationProjectionVersions.ClassificationV1, result.ProjectionVersion);
        Assert.Equal(new string('c', 64), result.StoreGenerationFingerprint);
        Assert.Equal("snap-1", result.PreflightSnapshotId);
        Assert.Equal("2026-08-01T12:00:00.0000000Z", result.PreflightExpiresAt);
        Assert.Equal(new string('d', 64), result.CategoryLifecycleFingerprint);
        Assert.Equal("2026-08-01T12:00:00.0000000Z", result.ExpiresAt);

        var json = JsonSerializer.Serialize(result, ClassifyJsonContext.Default.ClassifyApplyPreviewResult);
        Assert.Contains("evaluationFingerprint", json, StringComparison.Ordinal);
        Assert.Contains("selectedTransactionIds", json, StringComparison.Ordinal);
        Assert.Contains("contributingRuleVersionIds", json, StringComparison.Ordinal);
        Assert.Contains("exclusionCount", json, StringComparison.Ordinal);
        Assert.Contains("storeGenerationFingerprint", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceDescription", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signedAmount", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/home/", json, StringComparison.OrdinalIgnoreCase);

        var roundTrip = JsonSerializer.Deserialize(json, ClassifyJsonContext.Default.ClassifyApplyPreviewResult);
        Assert.NotNull(roundTrip);
        Assert.Equal(result.SelectedTransactionIds.ToArray(), roundTrip!.SelectedTransactionIds.ToArray());
        Assert.Equal(result.ContributingRuleVersionIds.ToArray(), roundTrip.ContributingRuleVersionIds.ToArray());
        Assert.Equal(result.EvaluationFingerprint, roundTrip.EvaluationFingerprint);
        Assert.Equal(result.ExclusionCount, roundTrip.ExclusionCount);
    }

    [Fact]
    public void Mapper_to_apply_preview_result_orders_by_ordinal_not_request_shuffle()
    {
        var preview = new ClassifyApplyPreviewRow(
            "prev-2", null, "eval-2", new string('1', 64),
            ClassifyContractMapper.SelectionModeExactRule, new string('2', 64),
            ActualsContractVersions.Current, ClassificationProjectionVersions.ClassificationV1,
            new string('3', 64), "snap-2", "2026-08-01T13:00:00.0000000Z",
            new string('4', 64), new string('5', 64), new string('6', 64),
            "2026-08-01T13:00:00.0000000Z", 2, 0, 0, 0, "actor", "2026-08-01T12:00:00.0000000Z");
        // Items provided out of ordinal order.
        var items = new[]
        {
            new ClassifyApplyPreviewItemRow(
                "prev-2", 1, "out-z", "tx-z", "assign", "cat-z", "rv-z",
                null, null, "tr", "rr", "ar", null),
            new ClassifyApplyPreviewItemRow(
                "prev-2", 0, "out-a", "tx-a", "assign", "cat-a", "rv-a",
                null, null, "tr", "rr", "ar", null)
        };

        var result = ClassifyContractMapper.ToApplyPreviewResult(preview, items, 2, 0);
        Assert.Equal(["tx-a", "tx-z"], result.SelectedTransactionIds.ToArray());
        Assert.Equal(["cat-a", "cat-z"], result.TargetCategoryIds.ToArray());
        Assert.Equal(["rv-a", "rv-z"], result.ContributingRuleVersionIds.ToArray());
        Assert.Equal(ClassifyContractMapper.SelectionModeExactRule, result.SelectionMode);
    }

    [Fact]
    public async Task Published_preview_discloses_complete_authorization_surface()
    {
        var seeded = await SeedSuggestionAsync("disclosure shop");
        var outcomes = await ListOutcomesAsync(seeded.EvaluationId);
        var suggestionIds = outcomes
            .Where(o => o.OutcomeType == "suggestion")
            .Select(o => o.OutcomeId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(suggestionIds);

        var result = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                seeded.EvaluationId,
                new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes, OutcomeIds: suggestionIds)),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var value = result.Value!;

        Assert.Equal(seeded.EvaluationId, value.EvaluationId);
        Assert.Equal(64, value.EvaluationFingerprint.Length);
        Assert.Equal(ClassifyContractMapper.SelectionModeSelectedOutcomes, value.SelectionMode);
        Assert.Equal(64, value.SelectionHash.Length);
        Assert.Equal(64, value.TargetCategoryFingerprint.Length);
        Assert.Equal(64, value.RuleAuthorityFingerprint.Length);
        Assert.Equal(value.SelectedCount, value.SelectedTransactionIds.Count);
        Assert.Equal(value.SelectedCount, value.TargetCategoryIds.Count);
        Assert.Equal(
            value.SelectedTransactionIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            value.SelectedTransactionIds.ToArray());
        Assert.Equal(
            value.ContributingRuleVersionIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            value.ContributingRuleVersionIds.ToArray());
        Assert.Contains(seeded.RuleVersionId, value.ContributingRuleVersionIds);
        Assert.All(value.TargetCategoryIds, id => Assert.False(string.IsNullOrWhiteSpace(id)));
        Assert.True(value.TargetCategoryIds.Distinct(StringComparer.Ordinal).Count() >= 1);
        Assert.Equal(ActualsContractVersions.Current, value.LedgerContractVersion);
        Assert.Equal(ClassificationProjectionVersions.ClassificationV1, value.ProjectionVersion);
        Assert.Equal(64, value.StoreGenerationFingerprint.Length);
        Assert.False(string.IsNullOrWhiteSpace(value.PreflightSnapshotId));
        Assert.Equal(value.PreflightExpiresAt, value.ExpiresAt);
        Assert.Equal(64, value.CategoryLifecycleFingerprint.Length);

        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        var row = await previewStore.GetPreviewAsync(connection, null, value.PreviewId, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal(row!.EvaluationFingerprint, value.EvaluationFingerprint);
        Assert.Equal(row.ExclusionCount, value.ExclusionCount);
        Assert.Equal(row.NoSuggestionCount, value.NoSuggestionCount);
        Assert.Equal(row.ConflictCount, value.ConflictCount);
        Assert.Equal(row.StoreGenerationFingerprint, value.StoreGenerationFingerprint);
        Assert.Equal(row.PreflightSnapshotId, value.PreflightSnapshotId);
        Assert.Equal(row.CategoryLifecycleFingerprint, value.CategoryLifecycleFingerprint);
        Assert.Equal(row.TargetCategoryFingerprint, value.TargetCategoryFingerprint);
        Assert.Equal(row.RuleAuthorityFingerprint, value.RuleAuthorityFingerprint);

        var items = await previewStore.ListItemsAsync(connection, null, value.PreviewId, CancellationToken.None);
        Assert.Equal(
            items.OrderBy(i => i.Ordinal).Select(i => i.TransactionId).ToArray(),
            value.SelectedTransactionIds.ToArray());
        Assert.Equal(
            items.OrderBy(i => i.Ordinal).Select(i => i.CategoryId).ToArray(),
            value.TargetCategoryIds.ToArray());
    }

    [Fact]
    public async Task Published_preview_orders_selected_transactions_deterministically()
    {
        var category = await CreateCategoryAsync("Order");
        var versionId = await SaveDraftAsync(category.CategoryId, "order-token");
        await ActivateWithGateAsync(versionId, category.CategoryId, "order-token", broadApply: false);
        var txA = await RecordAsync("order-token");
        var txB = await RecordAsync("order-token");
        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);
        var outcomes = await ListOutcomesAsync(evaluated.Value!.EvaluationId);
        var suggestionIds = outcomes.Where(o => o.OutcomeType == "suggestion").Select(o => o.OutcomeId).ToArray();
        Assert.True(suggestionIds.Length >= 2);

        // Deliberately reverse outcome id order in the request — public result must still be ordinal-stable.
        var shuffled = suggestionIds.OrderByDescending(id => id, StringComparer.Ordinal).ToArray();
        var first = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                evaluated.Value.EvaluationId,
                new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes, OutcomeIds: shuffled)),
            actor, NextKey(), CancellationToken.None);
        Assert.True(first.IsSuccess, first.ErrorCode);
        var second = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                evaluated.Value.EvaluationId,
                new ClassifyApplySelection(
                    ClassifyApplySelectionMode.SelectedOutcomes,
                    OutcomeIds: shuffled.Reverse().ToArray())),
            actor, NextKey(), CancellationToken.None);
        Assert.True(second.IsSuccess, second.ErrorCode);

        Assert.Equal(first.Value!.SelectedTransactionIds.ToArray(), second.Value!.SelectedTransactionIds.ToArray());
        Assert.Equal(first.Value.TargetCategoryIds.ToArray(), second.Value.TargetCategoryIds.ToArray());
        Assert.Equal(
            first.Value.SelectedTransactionIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            first.Value.SelectedTransactionIds.ToArray());
        Assert.Contains(txA.TransactionId, first.Value.SelectedTransactionIds);
        Assert.Contains(txB.TransactionId, first.Value.SelectedTransactionIds);
    }

    [Fact]
    public async Task Published_preview_excludes_private_payload_fields()
    {
        const string canary = "CANARY_PREVIEW_PRIVATE_DESC_xyz";
        var seeded = await SeedSuggestionAsync(canary);
        var outcomes = await ListOutcomesAsync(seeded.EvaluationId);
        var suggestionIds = outcomes.Where(o => o.OutcomeType == "suggestion").Select(o => o.OutcomeId).ToArray();
        var result = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                seeded.EvaluationId,
                new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes, OutcomeIds: suggestionIds)),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        var json = JsonSerializer.Serialize(result.Value, ClassifyJsonContext.Default.ClassifyApplyPreviewResult);
        Assert.DoesNotContain(canary, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceDescription", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signedAmount", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("normalizedValueHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/home/", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("originalDescription", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("evaluationFingerprint", json, StringComparison.Ordinal);
        Assert.Contains("selectedTransactionIds", json, StringComparison.Ordinal);
        Assert.Contains("contributingRuleVersionIds", json, StringComparison.Ordinal);
        Assert.Contains("storeGenerationFingerprint", json, StringComparison.Ordinal);
        Assert.Contains("exclusionCount", json, StringComparison.Ordinal);
        Assert.Contains("noSuggestionCount", json, StringComparison.Ordinal);
        Assert.Contains("conflictCount", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Published_preview_round_trips_complete_disclosure_json()
    {
        var seeded = await SeedSuggestionAsync("roundtrip shop");
        var outcomes = await ListOutcomesAsync(seeded.EvaluationId);
        var suggestionIds = outcomes.Where(o => o.OutcomeType == "suggestion").Select(o => o.OutcomeId).ToArray();
        var result = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                seeded.EvaluationId,
                new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes, OutcomeIds: suggestionIds)),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        var json = JsonSerializer.Serialize(result.Value, ClassifyJsonContext.Default.ClassifyApplyPreviewResult);
        var roundTrip = JsonSerializer.Deserialize(json, ClassifyJsonContext.Default.ClassifyApplyPreviewResult);
        Assert.NotNull(roundTrip);
        Assert.Equal(result.Value!.PreviewId, roundTrip!.PreviewId);
        Assert.Equal(result.Value.EvaluationFingerprint, roundTrip.EvaluationFingerprint);
        Assert.Equal(result.Value.SelectionMode, roundTrip.SelectionMode);
        Assert.Equal(result.Value.SelectedTransactionIds.ToArray(), roundTrip.SelectedTransactionIds.ToArray());
        Assert.Equal(result.Value.TargetCategoryIds.ToArray(), roundTrip.TargetCategoryIds.ToArray());
        Assert.Equal(result.Value.ContributingRuleVersionIds.ToArray(), roundTrip.ContributingRuleVersionIds.ToArray());
        Assert.Equal(result.Value.ExclusionCount, roundTrip.ExclusionCount);
        Assert.Equal(result.Value.NoSuggestionCount, roundTrip.NoSuggestionCount);
        Assert.Equal(result.Value.ConflictCount, roundTrip.ConflictCount);
        Assert.Equal(result.Value.StoreGenerationFingerprint, roundTrip.StoreGenerationFingerprint);
        Assert.Equal(result.Value.PreflightSnapshotId, roundTrip.PreflightSnapshotId);
        Assert.Equal(result.Value.CategoryLifecycleFingerprint, roundTrip.CategoryLifecycleFingerprint);
    }

    [Fact]
    public async Task Preview_does_not_persist_source_descriptions()
    {
        var seeded = await SeedSuggestionAsync("secret-description-token");
        var outcomes = await ListOutcomesAsync(seeded.EvaluationId);
        var ids = outcomes.Where(o => o.OutcomeType == "suggestion").Select(o => o.OutcomeId).ToArray();
        var result = await preview.HandleAsync(
            new ClassifyApplyPreviewRequest(
                ClassifyOperationIds.ContractVersion,
                seeded.EvaluationId,
                new ClassifyApplySelection(ClassifyApplySelectionMode.SelectedOutcomes, OutcomeIds: ids)),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT preview_id, selection_mode, actor FROM apply_preview WHERE preview_id = $id;";
        command.Parameters.AddWithValue("$id", result.Value!.PreviewId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var blob = reader.GetString(0) + reader.GetString(1) + reader.GetString(2);
        Assert.DoesNotContain("secret-description-token", blob, StringComparison.OrdinalIgnoreCase);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private sealed record SeededEval(
        string EvaluationId,
        string RuleVersionId,
        IReadOnlyList<string> TransactionIds);

    private async Task<SeededEval> SeedSuggestionAsync(string description, bool broadApply = false)
    {
        var category = await CreateCategoryAsync("Cat");
        var versionId = await SaveDraftAsync(category.CategoryId, description);
        await ActivateWithGateAsync(versionId, category.CategoryId, description, broadApply);
        var tx = await RecordAsync(description);
        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor, NextKey(), CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);
        Assert.True(evaluated.Value!.SuggestionCount >= 1);
        return new SeededEval(evaluated.Value.EvaluationId, versionId, [tx.TransactionId]);
    }

    private async Task ActivateWithGateAsync(
        string versionId,
        string categoryId,
        string description,
        bool broadApply)
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
                "apply preview activate"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(activated.IsSuccess, activated.ErrorCode);
        Assert.Equal(broadApply, activated.Value!.BroadApplyAllowed);
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
                "apply preview draft"),
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

    private async Task<IReadOnlyList<ClassifyOutcomeRow>> ListOutcomesAsync(string evaluationId)
    {
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        return await services.EvaluationStore.ListOutcomesAsync(connection, null, evaluationId, CancellationToken.None);
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

    private async Task AssignCategoryAsync(string transactionId, string categoryId, string reason)
    {
        _ = await ExecuteSuccessAsync(
            "ledger.transaction.category.assign",
            new AssignCategoryInput(transactionId, categoryId, reason),
            NextKey(),
            LedgerJsonContext.Default.AssignCategoryInput,
            LedgerJsonContext.Default.CategoryAllocationResult);
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
                new VoidTransactionInput(transactionId, "preview-void"),
                TransactionCorrectionJsonContext.Default.VoidTransactionInput),
            NextKey());
        var json = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var args = descriptor.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Concat(["--input", "-"]).ToArray();
        _ = await process.RunAsync(args, json, CancellationToken.None);
    }

    private async Task<AccountDetail> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        return await ExecuteSuccessAsync(
            "ledger.account.create",
            new CreateAccountInput("Preview Bank " + unique, "P-" + unique, AccountType.Cheque, "****" + unique[..4], "ZAR"),
            NextKey(), LedgerJsonContext.Default.CreateAccountInput, LedgerJsonContext.Default.AccountDetail);
    }

    private Task<CategoryDetail> CreateCategoryAsync(string name) =>
        ExecuteSuccessAsync(
            "ledger.category.create",
            new CreateCategoryInput(name + "-" + Guid.NewGuid().ToString("N")[..6]),
            NextKey(), LedgerJsonContext.Default.CreateCategoryInput, LedgerJsonContext.Default.CategoryDetail);

    private async Task ArchiveCategoryAsync(string categoryId)
    {
        _ = await ExecuteSuccessAsync(
            "ledger.category.archive",
            new ArchiveCategoryInput(categoryId, "apply preview reactivation check"),
            NextKey(),
            LedgerJsonContext.Default.ArchiveCategoryInput,
            LedgerJsonContext.Default.CategoryLifecycleResult);
    }

    private async Task ReactivateCategoryAsync(string categoryId)
    {
        _ = await ExecuteSuccessAsync(
            "ledger.category.reactivate",
            new ReactivateCategoryInput(categoryId, "apply preview reactivation check"),
            NextKey(),
            LedgerJsonContext.Default.ReactivateCategoryInput,
            LedgerJsonContext.Default.CategoryLifecycleResult);
    }

    private async Task<TransactionDetail> RecordAsync(string description)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"))));
        return await ExecuteSuccessAsync(
            "ledger.transaction.record",
            new RecordTransactionInput(
                accountId, "-12.34", "ZAR", "2026-07-15", null, description, null, null,
                new RegisterEvidenceInput(EvidenceKind.AgentCapture, digest, "preview:" + Guid.NewGuid().ToString("N")[..8], null, null)),
            NextKey(), LedgerJsonContext.Default.RecordTransactionInput, LedgerJsonContext.Default.TransactionDetail);
    }

    private async Task<TResult> ExecuteSuccessAsync<TInput, TResult>(
        string operationId, TInput input, string? key,
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

    private string NextKey() => $"apply-preview-{Interlocked.Increment(ref keySeq)}-{Guid.NewGuid():N}";
}
