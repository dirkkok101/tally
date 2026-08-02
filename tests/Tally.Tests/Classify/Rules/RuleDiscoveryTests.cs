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
using Tally.Domain.Classify.Discovery;
using Tally.Domain.Classify.Normalization;
using Tally.Domain.Ledger;
using Tally.Features.Classify.Contract;
using Tally.Features.Classify.Rules.Discovery;
using Tally.Features.Classify.Rules.Validate;
using Tally.Infrastructure.Classify.Corpus;
using Tally.Infrastructure.Classify.Storage.Rules;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Classify.Rules;

/// <summary>
/// TASK-CLASSIFY-ERGONOMICS-RULE-DISCOVERY / bd-2vbg —
/// rule.list filters/cursors/high-water and rule-set.active.get authority/privacy/no-mutation.
/// Synthetic isolated roots only.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class RuleDiscoveryTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-rule-discovery-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("automation", "rule-discovery", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private ClassifyRuleServices services = null!;
    private ListClassificationRulesQuery listQuery = null!;
    private GetActiveClassificationRuleSetQuery activeQuery = null!;
    private ValidateClassificationRuleCommand validate = null!;
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
        services = await ClassifyRuleExtensions.CreateServicesAsync(root, ledger, cancellationToken: CancellationToken.None);
        validate = new ValidateClassificationRuleCommand(
            services.State.Store,
            services.RuleStore,
            services.ValidationStore,
            ClassifyCorpusExtensions.CreateReader(),
            ledger,
            services.State.Idempotency);
        var discoveryStore = new ClassificationRuleDiscoveryStore();
        listQuery = new ListClassificationRulesQuery(
            services.State.Store,
            services.RuleStore,
            discoveryStore,
            services.RuleSetStore,
            ledger);
        activeQuery = new GetActiveClassificationRuleSetQuery(
            services.State.Store,
            services.RuleStore,
            discoveryStore,
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

    // ── Request validation ───────────────────────────────────────────────────

    [Fact]
    public async Task List_requires_actor()
    {
        var r = await listQuery.HandleAsync(new ClassifyRuleListRequest("1.0", 10), null, CancellationToken.None);
        Assert.Equal(ClassifyErrors.ActorRequired, r.ErrorCode);
        Assert.Null(r.Value);
    }

    [Fact]
    public async Task List_rejects_unsupported_version()
    {
        var r = await listQuery.HandleAsync(new ClassifyRuleListRequest("9.9", 10), actor, CancellationToken.None);
        Assert.Equal(ClassifyErrors.UnsupportedVersion, r.ErrorCode);
        Assert.Null(r.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task List_rejects_page_size_outside_bounds(int pageSize)
    {
        var r = await listQuery.HandleAsync(new ClassifyRuleListRequest("1.0", pageSize), actor, CancellationToken.None);
        Assert.Equal(ClassifyErrors.ResourceLimit, r.ErrorCode);
        Assert.Null(r.Value);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(500)]
    public async Task List_accepts_page_size_bounds(int pageSize)
    {
        var r = await listQuery.HandleAsync(new ClassifyRuleListRequest("1.0", pageSize), actor, CancellationToken.None);
        Assert.True(r.IsSuccess, r.ErrorCode);
        Assert.NotNull(r.Value);
    }

    [Fact]
    public async Task Active_get_requires_actor()
    {
        var r = await activeQuery.HandleAsync(new ClassifyRuleSetActiveGetRequest("1.0"), null, CancellationToken.None);
        Assert.Equal(ClassifyErrors.ActorRequired, r.ErrorCode);
        Assert.Null(r.Value);
    }

    [Fact]
    public async Task Active_get_rejects_unsupported_version()
    {
        var r = await activeQuery.HandleAsync(new ClassifyRuleSetActiveGetRequest("9.9"), actor, CancellationToken.None);
        Assert.Equal(ClassifyErrors.UnsupportedVersion, r.ErrorCode);
        Assert.Null(r.Value);
    }

    [Fact]
    public async Task Active_get_without_authority_returns_typed_not_found()
    {
        var before = await CaptureClassifyOracleAsync();
        var r = await activeQuery.HandleAsync(new ClassifyRuleSetActiveGetRequest("1.0"), actor, CancellationToken.None);
        Assert.Equal(ClassifyErrors.ActiveRuleSetNotFound, r.ErrorCode);
        Assert.Null(r.Value);
        await AssertNoMutationAsync(before);
    }

    // ── List empty / items / filters ─────────────────────────────────────────

    [Fact]
    public async Task Empty_catalogue_returns_zero_page()
    {
        var r = await listQuery.HandleAsync(new ClassifyRuleListRequest("1.0", 10), actor, CancellationToken.None);
        Assert.True(r.IsSuccess, r.ErrorCode);
        Assert.Equal(0, r.Value!.OverallCount);
        Assert.Equal(0, r.Value.FilteredCount);
        Assert.Equal(0, r.Value.ReturnedCount);
        Assert.Empty(r.Value.Items);
        Assert.Null(r.Value.Continuation);
    }

    [Fact]
    public async Task List_returns_dm_fields_for_draft_and_active()
    {
        var category = await CreateCategoryAsync("ListFields");
        var draftId = await SaveDraftAsync(category.CategoryId, "draft merchant", "rule-draft-1");
        var activeId = await SaveDraftAsync(category.CategoryId, "active merchant", "rule-active-1");
        await ActivateAsync(activeId, category.CategoryId, "active merchant");

        var r = await listQuery.HandleAsync(new ClassifyRuleListRequest("1.0", 50), actor, CancellationToken.None);
        Assert.True(r.IsSuccess, r.ErrorCode);
        Assert.True(r.Value!.OverallCount >= 2);
        Assert.Equal(r.Value.FilteredCount, r.Value.ReturnedCount);
        var draft = r.Value.Items.Single(i => i.RuleVersionId == draftId);
        Assert.Equal(ClassifyRuleLifecycleFilter.Draft, draft.EffectiveLifecycle);
        Assert.False(draft.ActiveMembership);
        Assert.Equal(category.CategoryId, draft.CategoryId);
        Assert.Equal(category.Name, draft.CategoryDisplayName);
        Assert.Equal(ClassifyCategoryLifecycleState.Active, draft.CategoryLifecycle);
        Assert.Equal(ClassifyRuleProvenanceKind.OwnerAuthored, draft.Provenance);
        Assert.False(string.IsNullOrWhiteSpace(draft.ScopeHash));
        Assert.NotEmpty(draft.Conditions);
        Assert.All(draft.Conditions, c => Assert.True(Enum.IsDefined(c.FieldKey)));

        var active = r.Value.Items.Single(i => i.RuleVersionId == activeId);
        Assert.Equal(ClassifyRuleLifecycleFilter.Active, active.EffectiveLifecycle);
        Assert.True(active.ActiveMembership);
        Assert.Null(active.PriorRuleVersionId);
    }

    [Fact]
    public async Task List_orders_by_created_at_then_rule_version_id()
    {
        var category = await CreateCategoryAsync("Order");
        var a = await SaveDraftAsync(category.CategoryId, "order a", "rule-ord-a");
        var b = await SaveDraftAsync(category.CategoryId, "order b", "rule-ord-b");
        var r = await listQuery.HandleAsync(new ClassifyRuleListRequest("1.0", 50), actor, CancellationToken.None);
        Assert.True(r.IsSuccess, r.ErrorCode);
        var ids = r.Value!.Items.Select(i => i.RuleVersionId).ToList();
        var idxA = ids.IndexOf(a);
        var idxB = ids.IndexOf(b);
        Assert.True(idxA >= 0 && idxB >= 0);
        // createdAt then ruleVersionId ordinal — later saves should sort by created_at then id
        for (var i = 1; i < r.Value.Items.Count; i++)
        {
            var prev = r.Value.Items[i - 1];
            var cur = r.Value.Items[i];
            Assert.True(
                string.CompareOrdinal(prev.CreatedAt, cur.CreatedAt) < 0
                || (string.Equals(prev.CreatedAt, cur.CreatedAt, StringComparison.Ordinal)
                    && string.CompareOrdinal(prev.RuleVersionId, cur.RuleVersionId) < 0));
        }
    }

    [Fact]
    public async Task Filter_logical_rule_id_ands()
    {
        var category = await CreateCategoryAsync("LogF");
        var v1 = await SaveDraftAsync(category.CategoryId, "log one", "logical-one");
        _ = await SaveDraftAsync(category.CategoryId, "log two", "logical-two");
        var r = await listQuery.HandleAsync(
            new ClassifyRuleListRequest("1.0", 50, LogicalRuleId: "logical-one"),
            actor,
            CancellationToken.None);
        Assert.True(r.IsSuccess, r.ErrorCode);
        Assert.All(r.Value!.Items, i => Assert.Equal("logical-one", i.LogicalRuleId));
        Assert.Contains(r.Value.Items, i => i.RuleVersionId == v1);
    }

    [Fact]
    public async Task Filter_category_id_ands()
    {
        var catA = await CreateCategoryAsync("CatA");
        var catB = await CreateCategoryAsync("CatB");
        _ = await SaveDraftAsync(catA.CategoryId, "in a", "rule-cat-a");
        _ = await SaveDraftAsync(catB.CategoryId, "in b", "rule-cat-b");
        var r = await listQuery.HandleAsync(
            new ClassifyRuleListRequest("1.0", 50, CategoryId: catA.CategoryId),
            actor,
            CancellationToken.None);
        Assert.True(r.IsSuccess, r.ErrorCode);
        Assert.All(r.Value!.Items, i => Assert.Equal(catA.CategoryId, i.CategoryId));
    }

    [Fact]
    public async Task Filter_lifecycle_draft_ands()
    {
        var category = await CreateCategoryAsync("LifeF");
        var draft = await SaveDraftAsync(category.CategoryId, "life draft", "rule-life-d");
        var active = await SaveDraftAsync(category.CategoryId, "life active", "rule-life-a");
        await ActivateAsync(active, category.CategoryId, "life active");
        var r = await listQuery.HandleAsync(
            new ClassifyRuleListRequest("1.0", 50, Lifecycle: ClassifyRuleLifecycleFilter.Draft),
            actor,
            CancellationToken.None);
        Assert.True(r.IsSuccess, r.ErrorCode);
        Assert.All(r.Value!.Items, i => Assert.Equal(ClassifyRuleLifecycleFilter.Draft, i.EffectiveLifecycle));
        Assert.Contains(r.Value.Items, i => i.RuleVersionId == draft);
        Assert.DoesNotContain(r.Value.Items, i => i.RuleVersionId == active);
    }

    [Fact]
    public async Task Filter_active_membership_true_ands()
    {
        var category = await CreateCategoryAsync("MemF");
        _ = await SaveDraftAsync(category.CategoryId, "mem draft", "rule-mem-d");
        var active = await SaveDraftAsync(category.CategoryId, "mem active", "rule-mem-a");
        await ActivateAsync(active, category.CategoryId, "mem active");
        var r = await listQuery.HandleAsync(
            new ClassifyRuleListRequest("1.0", 50, ActiveMembership: true),
            actor,
            CancellationToken.None);
        Assert.True(r.IsSuccess, r.ErrorCode);
        Assert.All(r.Value!.Items, i => Assert.True(i.ActiveMembership));
        Assert.Contains(r.Value.Items, i => i.RuleVersionId == active);
    }

    [Fact]
    public async Task Filter_active_membership_false_ands()
    {
        var category = await CreateCategoryAsync("MemN");
        var draft = await SaveDraftAsync(category.CategoryId, "memn draft", "rule-memn-d");
        var active = await SaveDraftAsync(category.CategoryId, "memn active", "rule-memn-a");
        await ActivateAsync(active, category.CategoryId, "memn active");
        var r = await listQuery.HandleAsync(
            new ClassifyRuleListRequest("1.0", 50, ActiveMembership: false),
            actor,
            CancellationToken.None);
        Assert.True(r.IsSuccess, r.ErrorCode);
        Assert.All(r.Value!.Items, i => Assert.False(i.ActiveMembership));
        Assert.Contains(r.Value.Items, i => i.RuleVersionId == draft);
    }

    // ── Paging / high-water ──────────────────────────────────────────────────

    [Fact]
    public async Task Keyset_paging_has_no_duplicates()
    {
        var category = await CreateCategoryAsync("Page");
        for (var i = 0; i < 5; i++)
        {
            await SaveDraftAsync(category.CategoryId, "page " + i, "rule-page-" + i);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        while (true)
        {
            var page = await listQuery.HandleAsync(
                new ClassifyRuleListRequest("1.0", 2, Continuation: cursor),
                actor,
                CancellationToken.None);
            Assert.True(page.IsSuccess, page.ErrorCode);
            foreach (var item in page.Value!.Items)
            {
                Assert.True(seen.Add(item.RuleVersionId));
            }

            cursor = page.Value.Continuation;
            if (cursor is null)
            {
                break;
            }
        }

        var full = await listQuery.HandleAsync(new ClassifyRuleListRequest("1.0", 500), actor, CancellationToken.None);
        Assert.True(full.IsSuccess, full.ErrorCode);
        Assert.Equal(full.Value!.FilteredCount, seen.Count);
    }

    [Fact]
    public async Task High_water_excludes_concurrent_appends_after_first_page()
    {
        var category = await CreateCategoryAsync("HW");
        for (var i = 0; i < 3; i++)
        {
            await SaveDraftAsync(category.CategoryId, "hw " + i, "rule-hw-" + i);
        }

        var first = await listQuery.HandleAsync(new ClassifyRuleListRequest("1.0", 1), actor, CancellationToken.None);
        Assert.True(first.IsSuccess, first.ErrorCode);
        Assert.NotNull(first.Value!.Continuation);

        // Concurrent append after first page
        var late = await SaveDraftAsync(category.CategoryId, "hw late", "rule-hw-late");

        // Walk remaining pages with frozen continuation
        var seen = new HashSet<string>(StringComparer.Ordinal) { first.Value.Items[0].RuleVersionId };
        var cursor = first.Value.Continuation;
        while (cursor is not null)
        {
            var page = await listQuery.HandleAsync(
                new ClassifyRuleListRequest("1.0", 1, Continuation: cursor),
                actor,
                CancellationToken.None);
            Assert.True(page.IsSuccess, page.ErrorCode);
            foreach (var item in page.Value!.Items)
            {
                Assert.True(seen.Add(item.RuleVersionId));
            }

            cursor = page.Value.Continuation;
        }

        Assert.DoesNotContain(late, seen);

        // Fresh list includes the late append
        var fresh = await listQuery.HandleAsync(new ClassifyRuleListRequest("1.0", 500), actor, CancellationToken.None);
        Assert.True(fresh.IsSuccess, fresh.ErrorCode);
        Assert.Contains(fresh.Value!.Items, i => i.RuleVersionId == late);
    }

    [Fact]
    public async Task Malformed_continuation_returns_cursor_invalid_null_result()
    {
        var before = await CaptureClassifyOracleAsync();
        var r = await listQuery.HandleAsync(
            new ClassifyRuleListRequest("1.0", 10, Continuation: "%%%bad%%%"),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.CursorInvalid, r.ErrorCode);
        Assert.Null(r.Value);
        await AssertNoMutationAsync(before);
    }

    [Fact]
    public async Task Filter_mismatch_continuation_returns_cursor_invalid()
    {
        var category = await CreateCategoryAsync("CursF");
        for (var i = 0; i < 3; i++)
        {
            await SaveDraftAsync(category.CategoryId, "cf " + i, "rule-cf-" + i);
        }

        var first = await listQuery.HandleAsync(new ClassifyRuleListRequest("1.0", 1), actor, CancellationToken.None);
        Assert.NotNull(first.Value!.Continuation);
        var r = await listQuery.HandleAsync(
            new ClassifyRuleListRequest(
                "1.0",
                1,
                Lifecycle: ClassifyRuleLifecycleFilter.Draft,
                Continuation: first.Value.Continuation),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.CursorInvalid, r.ErrorCode);
        Assert.Null(r.Value);
    }

    [Fact]
    public async Task Page_size_mismatch_continuation_returns_cursor_invalid()
    {
        var category = await CreateCategoryAsync("CursP");
        for (var i = 0; i < 3; i++)
        {
            await SaveDraftAsync(category.CategoryId, "cp " + i, "rule-cp-" + i);
        }

        var first = await listQuery.HandleAsync(new ClassifyRuleListRequest("1.0", 1), actor, CancellationToken.None);
        Assert.NotNull(first.Value!.Continuation);
        var r = await listQuery.HandleAsync(
            new ClassifyRuleListRequest("1.0", 2, Continuation: first.Value.Continuation),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.CursorInvalid, r.ErrorCode);
        Assert.Null(r.Value);
    }

    [Fact]
    public async Task Authority_change_invalidates_continuation()
    {
        var category = await CreateCategoryAsync("AuthC");
        for (var i = 0; i < 3; i++)
        {
            await SaveDraftAsync(category.CategoryId, "ac " + i, "rule-ac-" + i);
        }

        var first = await listQuery.HandleAsync(new ClassifyRuleListRequest("1.0", 1), actor, CancellationToken.None);
        Assert.NotNull(first.Value!.Continuation);

        var active = await SaveDraftAsync(category.CategoryId, "ac activate", "rule-ac-act");
        await ActivateAsync(active, category.CategoryId, "ac activate");

        var before = await CaptureClassifyOracleAsync();
        var r = await listQuery.HandleAsync(
            new ClassifyRuleListRequest("1.0", 1, Continuation: first.Value.Continuation),
            actor,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.CursorStale, r.ErrorCode);
        Assert.Null(r.Value);
        await AssertNoMutationAsync(before);
    }

    // ── Active rule set ──────────────────────────────────────────────────────

    [Fact]
    public async Task Active_get_returns_authority_summary_fields()
    {
        var category = await CreateCategoryAsync("ActSum");
        var versionId = await SaveDraftAsync(category.CategoryId, "act sum", "rule-act-sum");
        await ActivateAsync(versionId, category.CategoryId, "act sum");

        var r = await activeQuery.HandleAsync(new ClassifyRuleSetActiveGetRequest("1.0"), actor, CancellationToken.None);
        Assert.True(r.IsSuccess, r.ErrorCode);
        Assert.NotNull(r.Value);
        Assert.False(string.IsNullOrWhiteSpace(r.Value!.RuleSetVersionId));
        Assert.False(string.IsNullOrWhiteSpace(r.Value.ActivationId));
        Assert.False(string.IsNullOrWhiteSpace(r.Value.ValidationId));
        Assert.False(string.IsNullOrWhiteSpace(r.Value.NormalizationVersion));
        Assert.False(string.IsNullOrWhiteSpace(r.Value.ActivationEpoch));
        Assert.Equal(ClassifyActiveRuleSetLifecycleStatus.Active, r.Value.LifecycleStatus);
        Assert.False(string.IsNullOrWhiteSpace(r.Value.ActivatedAt));
        Assert.Null(r.Value.RetiredAt);
        Assert.Contains(versionId, r.Value.RuleVersionIds);
        Assert.Equal(
            r.Value.RuleVersionIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            r.Value.RuleVersionIds.ToArray());
        Assert.Contains(r.Value.Categories, c => c.CategoryId == category.CategoryId);
        var cat = r.Value.Categories.Single(c => c.CategoryId == category.CategoryId);
        Assert.Equal(category.Name, cat.DisplayName);
        Assert.Equal(ClassifyCategoryLifecycleState.Active, cat.Lifecycle);
        Assert.NotNull(r.Value.TrustedGateReceiptId);
        Assert.NotNull(r.Value.TrustedGateReceiptFingerprint);
    }

    [Fact]
    public async Task Active_membership_traces_to_immutable_rule_set_version()
    {
        var category = await CreateCategoryAsync("Trace");
        var versionId = await SaveDraftAsync(category.CategoryId, "trace m", "rule-trace");
        await ActivateAsync(versionId, category.CategoryId, "trace m");
        var active = await activeQuery.HandleAsync(new ClassifyRuleSetActiveGetRequest("1.0"), actor, CancellationToken.None);
        Assert.True(active.IsSuccess, active.ErrorCode);
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        var pointer = await services.RuleSetStore.GetActiveRuleSetAsync(connection, null, CancellationToken.None);
        Assert.NotNull(pointer);
        Assert.Equal(pointer!.RuleSetVersionId, active.Value!.RuleSetVersionId);
        var members = await services.RuleSetStore.ListMemberRuleVersionIdsAsync(
            connection, null, pointer.RuleSetVersionId, CancellationToken.None);
        Assert.Equal(
            members.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            active.Value.RuleVersionIds.ToArray());
    }

    [Fact]
    public async Task Broad_apply_false_when_members_lack_flag()
    {
        var category = await CreateCategoryAsync("BroadF");
        var versionId = await SaveDraftAsync(category.CategoryId, "broad f", "rule-broad-f");
        await ActivateAsync(versionId, category.CategoryId, "broad f", broad: false);
        var r = await activeQuery.HandleAsync(new ClassifyRuleSetActiveGetRequest("1.0"), actor, CancellationToken.None);
        Assert.True(r.IsSuccess, r.ErrorCode);
        Assert.False(r.Value!.BroadApplyAllowed);
    }

    // ── Privacy / no-mutation ────────────────────────────────────────────────

    [Fact]
    public async Task List_does_not_disclose_owner_reason_or_corpus_path()
    {
        var category = await CreateCategoryAsync("Priv");
        const string canary = "CANARY_OWNER_REASON_SECRET";
        var versionId = await SaveDraftAsync(category.CategoryId, "priv shop", "rule-priv", reason: canary);
        var r = await listQuery.HandleAsync(new ClassifyRuleListRequest("1.0", 50), actor, CancellationToken.None);
        Assert.True(r.IsSuccess, r.ErrorCode);
        var json = JsonSerializer.Serialize(r.Value, ClassifyJsonContext.Default.ClassifyRuleListResult);
        Assert.DoesNotContain(canary, json, StringComparison.Ordinal);
        Assert.DoesNotContain("reason", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("corpus", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(root, json, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/", json, StringComparison.Ordinal);
        Assert.Contains(versionId, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Active_get_does_not_disclose_owner_reason()
    {
        var category = await CreateCategoryAsync("PrivA");
        const string canary = "CANARY_ACTIVATE_REASON_SECRET";
        var versionId = await SaveDraftAsync(category.CategoryId, "priv a", "rule-priv-a");
        await ActivateAsync(versionId, category.CategoryId, "priv a", activateReason: canary);
        var r = await activeQuery.HandleAsync(new ClassifyRuleSetActiveGetRequest("1.0"), actor, CancellationToken.None);
        Assert.True(r.IsSuccess, r.ErrorCode);
        var json = JsonSerializer.Serialize(r.Value, ClassifyJsonContext.Default.ClassifyRuleSetActiveGetResult);
        Assert.DoesNotContain(canary, json, StringComparison.Ordinal);
        Assert.DoesNotContain("corpus", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Successful_list_does_not_mutate_classify_state()
    {
        var category = await CreateCategoryAsync("NomutL");
        _ = await SaveDraftAsync(category.CategoryId, "nomut l", "rule-nomut-l");
        var before = await CaptureClassifyOracleAsync();
        var r = await listQuery.HandleAsync(new ClassifyRuleListRequest("1.0", 50), actor, CancellationToken.None);
        Assert.True(r.IsSuccess, r.ErrorCode);
        await AssertNoMutationAsync(before);
    }

    [Fact]
    public async Task Failed_list_does_not_mutate_classify_state()
    {
        var before = await CaptureClassifyOracleAsync();
        var r = await listQuery.HandleAsync(
            new ClassifyRuleListRequest("1.0", 10, Continuation: "!!!"),
            actor,
            CancellationToken.None);
        Assert.False(r.IsSuccess);
        Assert.Null(r.Value);
        await AssertNoMutationAsync(before);
    }

    [Fact]
    public async Task Successful_active_get_does_not_mutate_classify_state()
    {
        var category = await CreateCategoryAsync("NomutA");
        var v = await SaveDraftAsync(category.CategoryId, "nomut a", "rule-nomut-a");
        await ActivateAsync(v, category.CategoryId, "nomut a");
        var before = await CaptureClassifyOracleAsync();
        var r = await activeQuery.HandleAsync(new ClassifyRuleSetActiveGetRequest("1.0"), actor, CancellationToken.None);
        Assert.True(r.IsSuccess, r.ErrorCode);
        await AssertNoMutationAsync(before);
    }

    [Fact]
    public async Task Deterministic_list_replay()
    {
        var category = await CreateCategoryAsync("Det");
        _ = await SaveDraftAsync(category.CategoryId, "det", "rule-det");
        var a = await listQuery.HandleAsync(new ClassifyRuleListRequest("1.0", 50), actor, CancellationToken.None);
        var b = await listQuery.HandleAsync(new ClassifyRuleListRequest("1.0", 50), actor, CancellationToken.None);
        Assert.True(a.IsSuccess && b.IsSuccess);
        Assert.Equal(
            a.Value!.Items.Select(i => (i.RuleVersionId, i.CreatedAt, i.EffectiveLifecycle)).ToArray(),
            b.Value!.Items.Select(i => (i.RuleVersionId, i.CreatedAt, i.EffectiveLifecycle)).ToArray());
    }

    [Fact]
    public void Filter_fingerprint_is_deterministic()
    {
        var a = ClassifyContractMapper.RuleListFilterFingerprint("r", ClassifyRuleLifecycleFilter.Active, "c", true);
        var b = ClassifyContractMapper.RuleListFilterFingerprint("r", ClassifyRuleLifecycleFilter.Active, "c", true);
        Assert.Equal(a, b);
        Assert.NotEqual(
            a,
            ClassifyContractMapper.RuleListFilterFingerprint("r", ClassifyRuleLifecycleFilter.Draft, "c", true));
    }

    [Fact]
    public void Mapper_public_lifecycle_maps_active_with_broad_apply()
    {
        Assert.Equal(
            ClassifyRuleLifecycleFilter.Active,
            ClassifyContractMapper.ToPublicLifecycle("active_with_broad_apply"));
        Assert.Equal(
            ClassifyRuleLifecycleFilter.Draft,
            ClassifyContractMapper.ToPublicLifecycle("draft"));
    }

    [Fact]
    public void Conditions_use_closed_grammar_only()
    {
        var condition = new ClassificationRuleConditionInput(
            0,
            ClassificationRuleFieldKey.DescriptionNormalized,
            ClassificationRulePredicateKind.Equals,
            ValueText: "x");
        var json = JsonSerializer.Serialize(condition, ClassifyJsonContext.Default.ClassificationRuleConditionInput);
        Assert.DoesNotContain("regex", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fuzzy", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Continuation_null_on_final_page()
    {
        var category = await CreateCategoryAsync("Final");
        _ = await SaveDraftAsync(category.CategoryId, "final", "rule-final");
        var r = await listQuery.HandleAsync(new ClassifyRuleListRequest("1.0", 500), actor, CancellationToken.None);
        Assert.True(r.IsSuccess, r.ErrorCode);
        Assert.Null(r.Value!.Continuation);
    }

    [Fact]
    public async Task Accounting_returned_equals_items_count()
    {
        var category = await CreateCategoryAsync("Acct");
        for (var i = 0; i < 4; i++)
        {
            await SaveDraftAsync(category.CategoryId, "acct " + i, "rule-acct-" + i);
        }

        var r = await listQuery.HandleAsync(new ClassifyRuleListRequest("1.0", 2), actor, CancellationToken.None);
        Assert.True(r.IsSuccess, r.ErrorCode);
        Assert.Equal(2, r.Value!.ReturnedCount);
        Assert.Equal(2, r.Value.Items.Count);
        Assert.True(r.Value.FilteredCount >= 4);
        Assert.NotNull(r.Value.Continuation);
    }

    [Fact]
    public async Task Empty_filter_intersection_returns_zero_items()
    {
        var category = await CreateCategoryAsync("EmptyI");
        _ = await SaveDraftAsync(category.CategoryId, "empty i", "rule-empty-i");
        var r = await listQuery.HandleAsync(
            new ClassifyRuleListRequest("1.0", 50, Lifecycle: ClassifyRuleLifecycleFilter.Retired),
            actor,
            CancellationToken.None);
        Assert.True(r.IsSuccess, r.ErrorCode);
        Assert.Equal(0, r.Value!.FilteredCount);
        Assert.Empty(r.Value.Items);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<(long Rules, long Versions, long Sets, string? Active)> CaptureClassifyOracleAsync()
    {
        await using var connection = await services.State.Store.OpenMigratedAsync(CancellationToken.None);
        var rules = await services.RuleStore.CountRulesAsync(connection, null, CancellationToken.None);
        var versions = await services.RuleStore.CountRuleVersionsAsync(connection, null, CancellationToken.None);
        var sets = await services.RuleSetStore.CountRuleSetVersionsAsync(connection, null, CancellationToken.None);
        var active = await services.RuleSetStore.GetActiveRuleSetAsync(connection, null, CancellationToken.None);
        return (rules, versions, sets, active?.RuleSetVersionId);
    }

    private async Task AssertNoMutationAsync((long Rules, long Versions, long Sets, string? Active) before)
    {
        var after = await CaptureClassifyOracleAsync();
        Assert.Equal(before, after);
    }

    private async Task<string> SaveDraftAsync(
        string categoryId,
        string description,
        string ruleId,
        string reason = "discovery draft")
    {
        var result = await services.Save.HandleAsync(
            new ClassifyRuleSaveRequest(
                ClassifyOperationIds.ContractVersion,
                ruleId,
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
                reason),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!.RuleVersionId;
    }

    private async Task ActivateAsync(
        string versionId,
        string categoryId,
        string description,
        bool broad = false,
        string activateReason = "discovery activate")
    {
        var path = await WriteBoundCorpusAsync([(description, "suggestion", categoryId)]);
        var rep = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(rep.IsSuccess, rep.ErrorCode);
        var replay = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(replay.IsSuccess, replay.ErrorCode);
        var hold = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(
                ClassifyOperationIds.ContractVersion, [versionId], path,
                rep.Value!.ValidationId, replay.Value!.ValidationId,
                10, 2, ExplicitBenefitDecision: broad ? "approve-broad" : "approve"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(hold.IsSuccess, hold.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(hold.Value!.OwnerRulebookGateReceiptId));
        var activated = await services.Activate.HandleAsync(
            new ClassifyRuleActivateRequest(
                ClassifyOperationIds.ContractVersion,
                rep.Value.ValidationId,
                hold.Value.OwnerRulebookGateReceiptId!,
                broad,
                activateReason),
            actor,
            NextKey(),
            CancellationToken.None);
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

    private async Task<AccountDetail> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        return await ExecuteSuccessAsync(
            "ledger.account.create",
            new CreateAccountInput("Discovery Bank " + unique, "P-" + unique, AccountType.Cheque, "****" + ((int)((uint)unique.GetHashCode() % 9000u) + 1000).ToString(), "ZAR"),
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
                new RegisterEvidenceInput(EvidenceKind.AgentCapture, digest, "disc:" + Guid.NewGuid().ToString("N")[..8], null, null)),
            NextKey(), LedgerJsonContext.Default.RecordTransactionInput, LedgerJsonContext.Default.TransactionDetail);
    }

    private string NextKey() => "disc-key-" + (++keySeq).ToString(CultureInfo.InvariantCulture);

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
