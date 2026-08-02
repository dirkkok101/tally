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
using Tally.Features.Classify.Contract;
using Tally.Features.Classify.Evaluation.Evaluate;
using Tally.Features.Classify.Unresolved.Report;
using Tally.Infrastructure.Classify.Storage.Evaluation;
using Tally.Infrastructure.Classify.Storage.Rules;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Classify.Unresolved;

/// <summary>
/// TASK-CLASSIFY-ERGONOMICS-UNRESOLVED-REPORT / bd-3ciw —
/// Join, grouping, stale/lifecycle, accounting, privacy, and zero-write tests.
/// Synthetic disposable roots only; never touches live Tally data.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class UnresolvedPatternReportTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-unresolved-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("automation", "unresolved-report", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private ClassifyEvaluationServices services = null!;
    private GetUnresolvedPatternReportQuery query = null!;
    private ClassificationUnresolvedStore unresolvedStore = null!;
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
        unresolvedStore = new ClassificationUnresolvedStore();
        query = new GetUnresolvedPatternReportQuery(
            services.State.Store,
            services.EvaluationStore,
            unresolvedStore,
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

    // ── Happy path join / grouping / accounting ──────────────────────────────

    [Fact]
    public async Task Repeated_no_suggestion_patterns_group_and_account()
    {
        var seeded = await SeedRepeatedNoSuggestionAsync("coffee shop", count: 3);
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, 25, 2),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var v = result.Value!;
        Assert.True(v.NoSuggestionOutcomeCount >= 3);
        Assert.Equal(v.NoSuggestionOutcomeCount, v.JoinedRowCount);
        Assert.Equal(v.JoinedRowCount, v.CandidateRowCount + v.BelowMinimumRowCount);
        Assert.True(v.ReturnedGroupCount >= 1);
        Assert.Equal(v.ReturnedGroupCount, v.Groups.Count);
        Assert.Equal(v.DistinctGroupCount, v.ReturnedGroupCount + v.OmittedGroupCount);
        Assert.Equal(25, v.BoundedRequestTopN);
        Assert.Equal(2, v.BoundedRequestMinimumCount);
        Assert.Equal(64, v.EvaluationFingerprint.Length);
        Assert.Equal(64, v.ProjectionFingerprint.Length);
        Assert.Equal(64, v.ReportFingerprint.Length);
        Assert.All(v.Groups, g =>
        {
            Assert.False(string.IsNullOrWhiteSpace(g.RepresentativeNormalizedDescription));
            Assert.False(string.IsNullOrWhiteSpace(g.AccountId));
            Assert.True(g.TransactionCount >= 2);
            Assert.Equal(64, g.GroupFingerprint.Length);
        });
    }

    [Fact]
    public async Task Top_n_truncates_returned_groups_with_omitted_count()
    {
        // Distinct descriptions each with 2 no_suggestion rows → multiple candidate groups.
        var seeded = await SeedManyDistinctNoSuggestionGroupsAsync(groupCount: 4, perGroup: 2);
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, TopN: 2, MinimumCount: 2),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(2, result.Value!.ReturnedGroupCount);
        Assert.True(result.Value.DistinctGroupCount >= 4);
        Assert.Equal(result.Value.DistinctGroupCount - 2, result.Value.OmittedGroupCount);
        Assert.Equal(2, result.Value.Groups.Count);
        Assert.Equal(1, result.Value.Groups[0].Rank);
        Assert.Equal(2, result.Value.Groups[1].Rank);
    }

    [Fact]
    public async Task Below_minimum_groups_are_counted_not_returned()
    {
        var seeded = await SeedRepeatedNoSuggestionAsync("lonely merchant", count: 1);
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, 10, 2),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.True(result.Value!.NoSuggestionOutcomeCount >= 1);
        Assert.Equal(0, result.Value.ReturnedGroupCount);
        Assert.Empty(result.Value.Groups);
        Assert.True(result.Value.BelowMinimumRowCount >= 1);
    }

    [Fact]
    public async Task Empty_no_suggestion_evaluation_returns_zero_groups()
    {
        // All suggestions — no no_suggestion identities.
        var category = await CreateCategoryAsync("AllSug");
        var seeded = await SeedSuggestionOnlyAsync("all sug phrase", category);
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, 10, 2),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(0, result.Value!.NoSuggestionOutcomeCount);
        Assert.Equal(0, result.Value.JoinedRowCount);
        Assert.Empty(result.Value.Groups);
    }

    [Fact]
    public async Task Account_filter_narrows_joined_rows()
    {
        var seeded = await SeedRepeatedNoSuggestionAsync("filter coffee", count: 2);
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest(
                "1.0",
                seeded.EvaluationId,
                10,
                2,
                AccountId: accountId),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.All(result.Value!.Groups, g => Assert.Equal(accountId, g.AccountId));
    }

    [Fact]
    public async Task Amount_direction_filter_expense_matches()
    {
        var seeded = await SeedRepeatedNoSuggestionAsync("dir coffee", count: 2);
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest(
                "1.0",
                seeded.EvaluationId,
                10,
                2,
                AmountDirection: ClassificationAmountDirection.Expense),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.All(result.Value!.Groups, g => Assert.Equal(ClassificationAmountDirection.Expense, g.AmountDirection));
    }

    [Fact]
    public async Task Report_is_deterministic_across_calls()
    {
        var seeded = await SeedRepeatedNoSuggestionAsync("det coffee", count: 3);
        var a = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, 10, 2),
            actor,
            CancellationToken.None);
        var b = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, 10, 2),
            actor,
            CancellationToken.None);
        Assert.True(a.IsSuccess && b.IsSuccess);
        Assert.Equal(a.Value!.ReportFingerprint, b.Value!.ReportFingerprint);
        // Semantic ProjectionFingerprint is stable across fresh equivalent queries (no snapshot binding).
        Assert.Equal(a.Value.ProjectionFingerprint, b.Value.ProjectionFingerprint);
        Assert.Equal(a.Value.EvaluationFingerprint, b.Value.EvaluationFingerprint);
        Assert.Equal(a.Value.CategoryLifecycleFingerprint, b.Value.CategoryLifecycleFingerprint);
        Assert.Equal(a.Value.RuleSetFingerprint, b.Value.RuleSetFingerprint);
        Assert.Equal(
            a.Value.Groups.Select(g => g.GroupFingerprint).ToArray(),
            b.Value.Groups.Select(g => g.GroupFingerprint).ToArray());
        Assert.Equal(
            a.Value.Groups.Select(g => g.RepresentativeNormalizedDescription).ToArray(),
            b.Value.Groups.Select(g => g.RepresentativeNormalizedDescription).ToArray());
    }

    [Fact]
    public async Task Fingerprints_are_hex_64()
    {
        var seeded = await SeedRepeatedNoSuggestionAsync("fp coffee", count: 2);
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, 10, 2),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(64, result.Value!.EvaluationFingerprint.Length);
        Assert.Equal(64, result.Value.ProjectionFingerprint.Length);
        Assert.Equal(64, result.Value.CategoryLifecycleFingerprint.Length);
        Assert.Equal(64, result.Value.RuleSetFingerprint.Length);
        Assert.Equal(64, result.Value.ReportFingerprint.Length);
    }

    // ── Typed failures / null result ─────────────────────────────────────────

    [Fact]
    public async Task Missing_evaluation_fails()
    {
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", "missing-eval", 10, 2),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.EvaluationNotFound, result.ErrorCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Missing_actor_fails()
    {
        var seeded = await SeedRepeatedNoSuggestionAsync("actor coffee", count: 2);
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, 10, 2),
            actor: null,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.ActorRequired, result.ErrorCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Unsupported_version_fails()
    {
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("9.9", "e", 10, 2),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.UnsupportedVersion, result.ErrorCode);
    }

    [Fact]
    public async Task Top_n_out_of_range_fails_resource_limit()
    {
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", "e", 0, 2),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.ResourceLimit, result.ErrorCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Top_n_above_500_fails()
    {
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", "e", 501, 2),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.ResourceLimit, result.ErrorCode);
    }

    [Fact]
    public async Task Minimum_count_below_2_fails()
    {
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", "e", 10, 1),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.ResourceLimit, result.ErrorCode);
    }

    [Fact]
    public async Task Minimum_count_above_500_fails()
    {
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", "e", 10, 501),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.ResourceLimit, result.ErrorCode);
    }

    [Fact]
    public async Task Empty_evaluation_id_fails()
    {
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", "  ", 10, 2),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.InvalidInput, result.ErrorCode);
    }

    [Fact]
    public async Task Abandoned_evaluation_fails_lifecycle()
    {
        // Completed runs cannot transition; insert a synthetic abandoned run row.
        var template = await SeedRepeatedNoSuggestionAsync("abandon coffee", count: 1);
        var synthId = await InsertSyntheticRunAsync(template.EvaluationId, lifecycle: "abandoned");
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", synthId, 10, 2),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.Lifecycle, result.ErrorCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Failed_evaluation_fails_lifecycle()
    {
        var template = await SeedRepeatedNoSuggestionAsync("fail coffee", count: 1);
        var synthId = await InsertSyntheticRunAsync(template.EvaluationId, lifecycle: "failed");
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", synthId, 10, 2),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.Lifecycle, result.ErrorCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Expired_snapshot_fails_stale()
    {
        // snapshot_expires_at is content-immutable after insert — seed with past expiry.
        var template = await SeedRepeatedNoSuggestionAsync("expire coffee", count: 1);
        var synthId = await InsertSyntheticRunAsync(
            template.EvaluationId,
            lifecycle: "completed",
            snapshotExpiresAt: "2000-01-01T00:00:00.0000000Z");
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", synthId, 10, 2),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.Stale, result.ErrorCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Voided_transaction_lifecycle_drift_fails_stale()
    {
        var seeded = await SeedRepeatedNoSuggestionAsync("void coffee", count: 2);
        await VoidTransactionAsync(seeded.TransactionIds[0]);
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, 10, 2),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.Stale, result.ErrorCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Retention_gap_count_mismatch_fails_integrity()
    {
        // Deterministic envelope/count mismatch: completed run claims no_suggestion_count=3
        // but has zero no_suggestion outcome rows. Requires Integrity + null + no writes.
        var template = await SeedRepeatedNoSuggestionAsync("gap coffee", count: 1);
        var synthId = await InsertSyntheticRunAsync(
            template.EvaluationId,
            lifecycle: "completed",
            noSuggestionCount: 3);
        var before = await CaptureTableCountsAsync();
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", synthId, 10, 2),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.Integrity, result.ErrorCode);
        Assert.Null(result.Value);
        Assert.Equal(before, await CaptureTableCountsAsync());
    }

    [Fact]
    public async Task Active_rule_set_drift_fails_stale_with_zero_writes()
    {
        var seeded = await SeedRepeatedNoSuggestionAsync("ruleset drift coffee", count: 2);
        // Activate a different rule set so active authority differs from retained evaluation.
        var category = await CreateCategoryAsync("DriftRS");
        var otherVersion = await SaveDraftAsync(category.CategoryId, "other-rule-phrase-xyz");
        await ActivateWithGateAsync(otherVersion, category.CategoryId, "other-rule-phrase-xyz");
        var before = await CaptureTableCountsAsync();
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, 10, 2),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.Stale, result.ErrorCode);
        Assert.Null(result.Value);
        Assert.Equal(before, await CaptureTableCountsAsync());
    }

    [Fact]
    public async Task Unsupported_retained_normalization_fails_stale()
    {
        var template = await SeedRepeatedNoSuggestionAsync("norm drift coffee", count: 1);
        var synthId = await InsertSyntheticRunAsync(
            template.EvaluationId,
            lifecycle: "completed",
            normalizationVersion: "normalization_v0_unsupported");
        var before = await CaptureTableCountsAsync();
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", synthId, 10, 2),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.Stale, result.ErrorCode);
        Assert.Null(result.Value);
        Assert.Equal(before, await CaptureTableCountsAsync());
    }

    [Fact]
    public async Task Category_lifecycle_archive_drift_fails_stale()
    {
        // Archive a category that was in the active catalogue at evaluation time.
        var seeded = await SeedRepeatedNoSuggestionWithCategoryAsync("archive-ns-phrase", count: 2);
        await ArchiveCategoryAsync(seeded.RuleCategoryId);
        var before = await CaptureTableCountsAsync();
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, 10, 2),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.Stale, result.ErrorCode);
        Assert.Null(result.Value);
        Assert.Equal(before, await CaptureTableCountsAsync());
    }

    [Fact]
    public async Task Category_lifecycle_reactivation_after_archive_fails_stale_for_prior_eval()
    {
        // FR-OUTCOME-INVALIDATION: reactivation of a category changes lifecycle fingerprint
        // relative to evaluations created before the reactivation event.
        var seeded = await SeedRepeatedNoSuggestionWithCategoryAsync("react coffee a", count: 2);
        await ArchiveCategoryAsync(seeded.RuleCategoryId);
        await ReactivateCategoryAsync(seeded.RuleCategoryId);
        var before = await CaptureTableCountsAsync();
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, 10, 2),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.Stale, result.ErrorCode);
        Assert.Null(result.Value);
        Assert.Equal(before, await CaptureTableCountsAsync());
    }

    [Fact]
    public async Task Required_category_history_read_failure_fails_closed_with_zero_writes()
    {
        // Production path: after a fresh projection is available, required post-eval
        // reactivation evidence is loaded via LedgerContractClient.GetBudgetCategoryAsync
        // (ledger.category.get, includeHistory:true). When that public read is unavailable,
        // unresolved.report must return a typed no-result error — never skip the proof.
        var seeded = await SeedRepeatedNoSuggestionWithCategoryAsync("hist fail coffee", count: 2);

        // Keep projection/actuals live; only make catalogue category.get host-unavailable so the
        // history loop cannot prove reactivation evidence from Ledger truth. RuntimeHandler
        // resolves ledger.category.* through CatalogueTransactions (which embeds Categories).
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        var sabotagedServices = LedgerServices.Create(database) with
        {
            Categories = null,
            CatalogueTransactions = null
        };
        var sabotagedProcess = new TallyProcess(registry, sabotagedServices);
        var sabotagedLedger = new LedgerContractClient(registry, sabotagedProcess);
        var sabotagedQuery = new GetUnresolvedPatternReportQuery(
            services.State.Store,
            services.EvaluationStore,
            unresolvedStore,
            services.RuleSetStore,
            sabotagedLedger);

        // Prove the real client path still delivers a usable projection with active categories.
        var projection = await sabotagedLedger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation,
            ActualsContractVersions.Current,
            actor,
            CancellationToken.None);
        Assert.True(projection.IsSuccess, projection.Error?.Code);
        Assert.NotNull(projection.Value);
        Assert.NotEmpty(projection.Value!.ActiveCategories!);
        var categoryId = projection.Value.ActiveCategories![0].CategoryId;
        Assert.False(string.IsNullOrWhiteSpace(categoryId));

        // Prove the same client path fails the required history evidence read.
        var historyRead = await sabotagedLedger.GetBudgetCategoryAsync(
            categoryId,
            CategoryContractVersions.Current,
            actor,
            CancellationToken.None,
            includeHistory: true);
        Assert.False(historyRead.IsSuccess);
        Assert.Null(historyRead.Value);

        var before = await CaptureTableCountsAsync();
        var result = await sabotagedQuery.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, 10, 2),
            actor,
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal(ClassifyErrors.LedgerUnavailable, result.ErrorCode);
        Assert.Null(result.Value);
        Assert.Equal(before, await CaptureTableCountsAsync());
    }

    // ── Privacy / zero-write ─────────────────────────────────────────────────

    [Fact]
    public async Task Result_excludes_transaction_ids_and_raw_descriptions()
    {
        const string canary = "CANARY_RAW_UNRESOLVED_DESC_zzz";
        var seeded = await SeedRepeatedNoSuggestionAsync(canary, count: 2);
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, 10, 2),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var json = JsonSerializer.Serialize(result.Value, ClassifyJsonContext.Default.ClassifyUnresolvedReportResult);
        Assert.DoesNotContain(canary, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceDescription", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transactionId", json, StringComparison.OrdinalIgnoreCase);
        foreach (var tx in seeded.TransactionIds)
        {
            Assert.DoesNotContain(tx, json, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(root, json, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Successful_report_does_not_write_classify_tables()
    {
        var seeded = await SeedRepeatedNoSuggestionAsync("nomut coffee", count: 2);
        var before = await CaptureTableCountsAsync();
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, 10, 2),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(before, await CaptureTableCountsAsync());
    }

    [Fact]
    public async Task Failed_report_does_not_write_classify_tables()
    {
        var seeded = await SeedRepeatedNoSuggestionAsync("nomut fail coffee", count: 2);
        var before = await CaptureTableCountsAsync();
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", "missing", 10, 2),
            actor,
            CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(before, await CaptureTableCountsAsync());
    }

    [Fact]
    public async Task Store_lists_only_no_suggestion_identities()
    {
        var category = await CreateCategoryAsync("Mix");
        // Rule phrase matches only one of two txs.
        var seeded = await SeedMixedAsync("match phrase", "no match phrase", category);
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        var identities = await unresolvedStore.ListNoSuggestionIdentitiesAsync(
            connection, null, seeded.EvaluationId, CancellationToken.None);
        Assert.NotEmpty(identities);
        Assert.All(identities, i =>
        {
            Assert.False(string.IsNullOrWhiteSpace(i.TransactionId));
            Assert.False(string.IsNullOrWhiteSpace(i.ItemLifecycleFingerprint));
            Assert.Equal(seeded.EvaluationId, i.EvaluationId);
        });
        // No description field on identity type.
        Assert.Null(typeof(ClassificationUnresolvedStore.NoSuggestionIdentity).GetProperty("SourceDescription"));
    }

    [Fact]
    public async Task Mapper_amount_direction_round_trips()
    {
        Assert.Equal(
            ClassificationAmountDirection.Expense,
            ClassifyContractMapper.MapAmountDirectionToWire("expense"));
        Assert.Equal(
            "income",
            ClassifyContractMapper.FormatUnresolvedAmountDirection(ClassificationAmountDirection.Income));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Groups_never_include_rule_or_proposal_fields()
    {
        var seeded = await SeedRepeatedNoSuggestionAsync("props coffee", count: 2);
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, 10, 2),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var json = JsonSerializer.Serialize(result.Value, ClassifyJsonContext.Default.ClassifyUnresolvedReportResult);
        Assert.DoesNotContain("proposal", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ruleVersion", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("activation", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("feedback", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Checked_amount_totals_are_consistent()
    {
        var seeded = await SeedRepeatedNoSuggestionAsync("amt coffee", count: 3);
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, 10, 2),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        foreach (var g in result.Value!.Groups)
        {
            Assert.True(g.CheckedAbsoluteAmountMinorTotal >= 0);
            // Expense txs are negative signed totals in magnitude terms.
            Assert.True(Math.Abs(g.CheckedSignedAmountMinorTotal) <= g.CheckedAbsoluteAmountMinorTotal
                || g.CheckedSignedAmountMinorTotal == -g.CheckedAbsoluteAmountMinorTotal
                || g.CheckedAbsoluteAmountMinorTotal >= Math.Abs(g.CheckedSignedAmountMinorTotal));
        }
    }

    [Fact]
    public async Task Income_direction_zero_wire_mapping_helpers()
    {
        Assert.Equal(
            ClassificationAmountDirection.Zero,
            ClassifyContractMapper.MapAmountDirectionToWire("zero"));
        Assert.Equal(
            "zero",
            ClassifyContractMapper.FormatUnresolvedAmountDirection(ClassificationAmountDirection.Zero));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Projection_fingerprint_excludes_snapshot_and_is_stable_for_same_semantics()
    {
        var gen = new string('a', 64);
        var cat = new string('b', 64);
        var ordered = new string('c', 64);
        var a = ClassifyContractMapper.ComputeUnresolvedProjectionFingerprint(
            "1.0", "classification_v1", gen, cat, ordered);
        var b = ClassifyContractMapper.ComputeUnresolvedProjectionFingerprint(
            "1.0", "classification_v1", gen, cat, ordered);
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
        // Generation drift changes the semantic fingerprint.
        var c = ClassifyContractMapper.ComputeUnresolvedProjectionFingerprint(
            "1.0", "classification_v1", new string('d', 64), cat, ordered);
        Assert.NotEqual(a, c);
        // Category lifecycle drift changes the semantic fingerprint.
        var d = ClassifyContractMapper.ComputeUnresolvedProjectionFingerprint(
            "1.0", "classification_v1", gen, new string('e', 64), ordered);
        Assert.NotEqual(a, d);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Contract_version_echoed_on_success()
    {
        var seeded = await SeedRepeatedNoSuggestionAsync("ver coffee", count: 2);
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, 10, 2),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(ClassifyOperationIds.ContractVersion, result.Value!.ContractVersion);
        Assert.Equal(seeded.EvaluationId, result.Value.EvaluationId);
    }

    [Fact]
    public async Task Normalization_version_is_present()
    {
        var seeded = await SeedRepeatedNoSuggestionAsync("norm coffee", count: 2);
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, 10, 2),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Value!.NormalizationVersion));
    }

    [Fact]
    public async Task Unknown_account_filter_returns_empty_groups_not_error()
    {
        var seeded = await SeedRepeatedNoSuggestionAsync("acct miss coffee", count: 2);
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest(
                "1.0",
                seeded.EvaluationId,
                10,
                2,
                AccountId: "acct-does-not-exist"),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Empty(result.Value!.Groups);
        Assert.Equal(0, result.Value.JoinedRowCount);
    }

    [Fact]
    public async Task Income_direction_filter_with_only_expense_rows_is_empty()
    {
        var seeded = await SeedRepeatedNoSuggestionAsync("income filter coffee", count: 2);
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest(
                "1.0",
                seeded.EvaluationId,
                10,
                2,
                AmountDirection: ClassificationAmountDirection.Income),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Empty(result.Value!.Groups);
    }

    [Fact]
    public async Task Representative_description_is_normalized_not_raw()
    {
        var seeded = await SeedRepeatedNoSuggestionAsync("Coffee   SHOP!!!", count: 2);
        var result = await query.HandleAsync(
            new ClassifyUnresolvedReportRequest("1.0", seeded.EvaluationId, 10, 2),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.NotEmpty(result.Value!.Groups);
        var rep = result.Value.Groups[0].RepresentativeNormalizedDescription;
        Assert.DoesNotContain("!", rep, StringComparison.Ordinal);
        Assert.Equal(rep, rep.ToLowerInvariant());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed record SeededEval(
        string EvaluationId,
        IReadOnlyList<string> TransactionIds,
        string RuleCategoryId = "");

    private async Task<SeededEval> SeedRepeatedNoSuggestionAsync(string unmatchedPhrase, int count) =>
        await SeedRepeatedNoSuggestionWithCategoryAsync(unmatchedPhrase, count);

    private async Task<SeededEval> SeedRepeatedNoSuggestionWithCategoryAsync(string unmatchedPhrase, int count)
    {
        var category = await CreateCategoryAsync("NS");
        var rulePhrase = "never-match-" + Guid.NewGuid().ToString("N")[..8];
        var versionId = await SaveDraftAsync(category.CategoryId, rulePhrase);
        await ActivateWithGateAsync(versionId, category.CategoryId, rulePhrase);
        var txs = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            txs.Add((await RecordAsync(unmatchedPhrase)).TransactionId);
        }

        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);
        Assert.True(evaluated.Value!.NoSuggestionCount >= count, evaluated.Value.NoSuggestionCount.ToString());
        return new SeededEval(evaluated.Value.EvaluationId, txs, category.CategoryId);
    }

    private async Task<SeededEval> SeedManyDistinctNoSuggestionGroupsAsync(int groupCount, int perGroup)
    {
        var category = await CreateCategoryAsync("Many");
        var versionId = await SaveDraftAsync(category.CategoryId, "never-match-many");
        await ActivateWithGateAsync(versionId, category.CategoryId, "never-match-many");
        var txs = new List<string>();
        for (var g = 0; g < groupCount; g++)
        {
            var phrase = "merchant group " + g.ToString(CultureInfo.InvariantCulture) + " unique";
            for (var i = 0; i < perGroup; i++)
            {
                txs.Add((await RecordAsync(phrase)).TransactionId);
            }
        }

        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);
        return new SeededEval(evaluated.Value!.EvaluationId, txs);
    }

    private async Task<SeededEval> SeedSuggestionOnlyAsync(string description, CategoryDetail category)
    {
        var versionId = await SaveDraftAsync(category.CategoryId, description);
        await ActivateWithGateAsync(versionId, category.CategoryId, description);
        var tx = await RecordAsync(description);
        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);
        Assert.True(evaluated.Value!.SuggestionCount >= 1);
        return new SeededEval(evaluated.Value.EvaluationId, [tx.TransactionId]);
    }

    private async Task<SeededEval> SeedMixedAsync(
        string matchPhrase,
        string unmatchedPhrase,
        CategoryDetail category)
    {
        var versionId = await SaveDraftAsync(category.CategoryId, matchPhrase);
        await ActivateWithGateAsync(versionId, category.CategoryId, matchPhrase);
        var matched = await RecordAsync(matchPhrase);
        var unmatched = await RecordAsync(unmatchedPhrase);
        var evaluated = await services.Evaluate.HandleAsync(
            new ClassifyEvaluateRequest(ClassifyOperationIds.ContractVersion),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(evaluated.IsSuccess, evaluated.ErrorCode);
        return new SeededEval(evaluated.Value!.EvaluationId, [matched.TransactionId, unmatched.TransactionId]);
    }

    /// <summary>
    /// Clone envelope fields from an existing completed run into a synthetic evaluation_run
    /// with the requested lifecycle / expiry (completed content is otherwise immutable).
    /// </summary>
    private async Task<string> InsertSyntheticRunAsync(
        string templateEvaluationId,
        string lifecycle,
        string? snapshotExpiresAt = null,
        int noSuggestionCount = 0,
        string? normalizationVersion = null)
    {
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        await using var read = connection.CreateCommand();
        read.CommandText = """
            SELECT rule_set_version_id, normalization_version, ledger_contract_version, projection_version,
                   store_generation_fingerprint, snapshot_id, snapshot_expires_at,
                   category_lifecycle_fingerprint, ordered_items_fingerprint, actor
            FROM evaluation_run WHERE evaluation_id = $id;
            """;
        read.Parameters.AddWithValue("$id", templateEvaluationId);
        await using var reader = await read.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        var ruleSet = reader.GetString(0);
        var norm = normalizationVersion ?? reader.GetString(1);
        var ledgerCv = reader.GetString(2);
        var proj = reader.GetString(3);
        var gen = reader.GetString(4);
        var snap = reader.GetString(5);
        var exp = snapshotExpiresAt ?? reader.GetString(6);
        var cat = reader.GetString(7);
        var ordered = reader.GetString(8);
        var act = reader.GetString(9);
        await reader.DisposeAsync();

        var synthId = "synth-" + Guid.NewGuid().ToString("N");
        // input_count must equal sum of outcome counts for envelope integrity check.
        var inputCount = noSuggestionCount;
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO evaluation_run (
                evaluation_id, operation_idempotency_key, rule_set_version_id, normalization_version,
                ledger_contract_version, projection_version, store_generation_fingerprint, snapshot_id,
                snapshot_expires_at, category_lifecycle_fingerprint, ordered_items_fingerprint,
                input_count, suggestion_count, no_suggestion_count, conflict_count, stale_count,
                lifecycle_state, actor, created_at
            ) VALUES (
                $id, NULL, $rs, $norm, $ledger, $proj, $gen, $snap, $exp, $cat, $ord,
                $input, 0, $ns, 0, 0, $life, $actor, $created
            );
            """;
        insert.Parameters.AddWithValue("$id", synthId);
        insert.Parameters.AddWithValue("$rs", ruleSet);
        insert.Parameters.AddWithValue("$norm", norm);
        insert.Parameters.AddWithValue("$ledger", ledgerCv);
        insert.Parameters.AddWithValue("$proj", proj);
        insert.Parameters.AddWithValue("$gen", gen);
        insert.Parameters.AddWithValue("$snap", snap);
        insert.Parameters.AddWithValue("$exp", exp);
        insert.Parameters.AddWithValue("$cat", cat);
        insert.Parameters.AddWithValue("$ord", ordered);
        insert.Parameters.AddWithValue("$input", inputCount);
        insert.Parameters.AddWithValue("$ns", noSuggestionCount);
        insert.Parameters.AddWithValue("$life", lifecycle);
        insert.Parameters.AddWithValue("$actor", act);
        insert.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await insert.ExecuteNonQueryAsync(CancellationToken.None);
        return synthId;
    }

    private async Task ArchiveCategoryAsync(string categoryId) =>
        _ = await ExecuteSuccessAsync(
            "ledger.category.archive",
            new ArchiveCategoryInput(categoryId, "unresolved-archive"),
            NextKey(),
            LedgerJsonContext.Default.ArchiveCategoryInput,
            LedgerJsonContext.Default.CategoryLifecycleResult);

    private async Task ReactivateCategoryAsync(string categoryId) =>
        _ = await ExecuteSuccessAsync(
            "ledger.category.reactivate",
            new ReactivateCategoryInput(categoryId, "unresolved-reactivate"),
            NextKey(),
            LedgerJsonContext.Default.ReactivateCategoryInput,
            LedgerJsonContext.Default.CategoryLifecycleResult);

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
                new VoidTransactionInput(transactionId, "unresolved-void"),
                TransactionCorrectionJsonContext.Default.VoidTransactionInput),
            NextKey());
        var json = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var args = descriptor.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Concat(["--input", "-"]).ToArray();
        _ = await process.RunAsync(args, json, CancellationToken.None);
    }

    private async Task<string> CaptureTableCountsAsync()
    {
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        async Task<long> Count(string table)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM " + table + ";";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture);
        }

        return string.Join(
            "|",
            await Count("evaluation_run"),
            await Count("classification_outcome"),
            await Count("match_evidence"),
            await Count("operation_idempotency"),
            await Count("apply_preview"));
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
                "unresolved activate"),
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
                "unresolved draft"),
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

    private async Task<AccountDetail> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        return await ExecuteSuccessAsync(
            "ledger.account.create",
            new CreateAccountInput("Unresolved Bank " + unique, "P-" + unique, AccountType.Cheque, "****" + ((int)((uint)unique.GetHashCode() % 9000u) + 1000).ToString(), "ZAR"),
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
                new RegisterEvidenceInput(EvidenceKind.AgentCapture, digest, "unresolved:" + Guid.NewGuid().ToString("N")[..8], null, null)),
            NextKey(), LedgerJsonContext.Default.RecordTransactionInput, LedgerJsonContext.Default.TransactionDetail);
    }

    private string NextKey() => "unresolved-key-" + (++keySeq).ToString(CultureInfo.InvariantCulture);

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
