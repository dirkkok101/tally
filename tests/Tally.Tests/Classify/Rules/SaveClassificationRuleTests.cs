using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Classify.Rules;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Categories;
using Tally.Domain.Classify.Normalization;
using Tally.Domain.Classify.Rules;
using Tally.Domain.Ledger;
using Tally.Features.Classify.Contract;
using Tally.Features.Classify.Rules.Save;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Rules;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Classify.Rules;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-RULE-DRAFT-SAVE / FR-CLASSIFY-RULE-LIFECYCLE / bd-1qb6
/// classify.rule.save: canonical conditions, active category via public Ledger client,
/// immutable draft persistence, attribution, idempotency, zero activation / Ledger mutation.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class SaveClassificationRuleTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-classify-rule-save-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("automation", "rule-save", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private ClassifyStateStore store = null!;
    private ClassificationRuleStore ruleStore = null!;
    private SaveClassificationRuleCommand command = null!;
    private ManualTimeProvider clock = null!;
    private int keySeq;
    private int ruleSeq;

    public async Task InitializeAsync()
    {
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        registry = OperationRegistry.Create();
        process = new TallyProcess(registry, LedgerServices.Create(database));
        ledger = new LedgerContractClient(registry, process);

        var classify = await ClassifyStateExtensions.CreateStateAsync(root, CancellationToken.None);
        store = classify.Store;
        ruleStore = new ClassificationRuleStore();
        clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero));
        command = new SaveClassificationRuleCommand(store, ruleStore, ledger, classify.Idempotency, clock);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ── Success ──────────────────────────────────────────────────────────────

    // FR-CLASSIFY-RULE-LIFECYCLE / AC: save supported rule over active category
    [Fact]
    public async Task Valid_canonical_rule_appends_immutable_draft_with_attribution_and_scope()
    {
        var category = await CreateCategoryAsync("Groceries");
        var result = await HandleAsync(
            NewRuleId(),
            category.CategoryId,
            [TextCondition(0, ClassificationRuleFieldKey.DescriptionNormalized, ClassificationRulePredicateKind.ContainsTokenSequence, "WHOLE FOODS")],
            reason: "owner groceries rule");

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(ClassifyOperationIds.ContractVersion, result.Value!.ContractVersion);
        Assert.Equal(category.CategoryId, result.Value.CategoryId);
        Assert.Equal(NormalizationDescriptor.V1.Version, result.Value.NormalizationVersion);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.RuleVersionId));

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var version = await ruleStore.GetRuleVersionAsync(connection, null, result.Value.RuleVersionId, CancellationToken.None);
        Assert.NotNull(version);
        Assert.Equal(ClassificationRuleStore.LifecycleDraft, version!.LifecycleState);
        Assert.Equal(ClassificationRuleStore.OriginOwnerAuthored, version.RuleOrigin);
        Assert.Equal(0, version.BroadApplyAllowed);
        Assert.Equal(64, version.ScopeHash.Length);
        Assert.Equal("owner groceries rule", version.Reason);
        Assert.Contains("automation:rule-save", version.CreatedBy, StringComparison.Ordinal);
        Assert.Null(version.PriorVersionId);
        Assert.Null(version.SourceFeedbackId);
        Assert.Null(version.ValidationRunId);

        var conditions = await ruleStore.ListConditionsAsync(connection, null, version.RuleVersionId, CancellationToken.None);
        Assert.Single(conditions);
        Assert.Equal(ClassificationRuleVocabulary.DescriptionNormalized, conditions[0].FieldKey);
        Assert.Equal(ClassificationRuleVocabulary.ContainsTokenSequencePredicate, conditions[0].PredicateKind);
        // Normalized description text is persisted (not raw casing).
        Assert.Equal("whole foods", conditions[0].ValueText);
    }

    // FR-CLASSIFY-RULE-LIFECYCLE / AC: no activation
    [Fact]
    public async Task Save_does_not_change_active_rule_set_pointer()
    {
        var category = await CreateCategoryAsync("Travel");
        Assert.Null(await GetActivePointerAsync());

        var result = await HandleAsync(
            NewRuleId(),
            category.CategoryId,
            [TextCondition(0, ClassificationRuleFieldKey.AccountId, ClassificationRulePredicateKind.Equals, "acct-1")]);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Null(await GetActivePointerAsync());
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM rule_set_version;"));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM active_rule_set;"));
    }

    // FR-CLASSIFY-RULE-LIFECYCLE / AC: no Ledger mutation
    [Fact]
    public async Task Save_does_not_invoke_ledger_category_mutation()
    {
        var category = await CreateCategoryAsync("Bills");
        var beforeName = category.Name;
        var result = await HandleAsync(
            NewRuleId(),
            category.CategoryId,
            [DirectionCondition(0, ClassificationAmountDirectionValue.Outflow)]);

        Assert.True(result.IsSuccess, result.ErrorCode);
        var listed = await ledger.ListClassificationCategoriesAsync("1.0", actor, CancellationToken.None, status: null);
        Assert.True(listed.IsSuccess);
        var after = Assert.Single(listed.Value!.Items, x => x.CategoryId == category.CategoryId);
        Assert.Equal(beforeName, after.Name);
        Assert.Equal(CategoryStatus.Active, after.Status);
    }

    // FR-CLASSIFY-RULE-LIFECYCLE / AC: prior version reference
    [Fact]
    public async Task Successor_draft_records_prior_version_reference_without_mutating_prior()
    {
        var category = await CreateCategoryAsync("Fuel");
        var ruleId = NewRuleId();
        var first = await HandleAsync(
            ruleId,
            category.CategoryId,
            [TextCondition(0, ClassificationRuleFieldKey.DescriptionNormalized, ClassificationRulePredicateKind.Equals, "SHELL")],
            reason: "v1",
            key: "k-prior-1");
        Assert.True(first.IsSuccess, first.ErrorCode);

        var second = await HandleAsync(
            ruleId,
            category.CategoryId,
            [TextCondition(0, ClassificationRuleFieldKey.DescriptionNormalized, ClassificationRulePredicateKind.StartsWith, "shell")],
            reason: "v2",
            priorVersionId: first.Value!.RuleVersionId,
            key: "k-prior-2");
        Assert.True(second.IsSuccess, second.ErrorCode);
        Assert.NotEqual(first.Value.RuleVersionId, second.Value!.RuleVersionId);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var prior = await ruleStore.GetRuleVersionAsync(connection, null, first.Value.RuleVersionId, CancellationToken.None);
        var successor = await ruleStore.GetRuleVersionAsync(connection, null, second.Value.RuleVersionId, CancellationToken.None);
        Assert.Equal("v1", prior!.Reason);
        Assert.Equal(first.Value.RuleVersionId, successor!.PriorVersionId);
        Assert.Equal(ClassificationRuleStore.LifecycleDraft, prior.LifecycleState);
        Assert.Equal(ClassificationRuleStore.LifecycleDraft, successor.LifecycleState);
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM classification_rule;"));
        Assert.Equal(2L, await CountAsync("SELECT COUNT(*) FROM rule_version;"));
    }

    // Multi-condition AND rule
    [Fact]
    public async Task Multi_condition_and_rule_persists_all_ordered_conditions()
    {
        var category = await CreateCategoryAsync("Multi");
        var result = await HandleAsync(
            NewRuleId(),
            category.CategoryId,
            [
                TextCondition(1, ClassificationRuleFieldKey.DescriptionNormalized, ClassificationRulePredicateKind.ContainsTokenSequence, "uber trip"),
                DirectionCondition(0, ClassificationAmountDirectionValue.Outflow),
                MinorEqualsCondition(2, 1_500)
            ]);

        Assert.True(result.IsSuccess, result.ErrorCode);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var conditions = await ruleStore.ListConditionsAsync(connection, null, result.Value!.RuleVersionId, CancellationToken.None);
        Assert.Equal(3, conditions.Count);
        Assert.Equal([0, 1, 2], conditions.Select(c => c.Ordinal).ToArray());
    }

    // ── Field / vocabulary errors (create no activatable version) ────────────

    [Fact]
    public async Task Empty_conditions_return_stable_field_error_and_create_no_version()
    {
        var category = await CreateCategoryAsync("Empty");
        var result = await HandleAsync(NewRuleId(), category.CategoryId, []);
        Assert.Equal(RuleVocabularyErrors.EmptyRule, result.ErrorCode);
        await AssertNoRuleMutationAsync();
    }

    [Fact]
    public async Task Unknown_field_returns_stable_field_error()
    {
        // Boundary cannot invent unknown enum members; force via invalid predicate/field combo on known field
        // by constructing vocabulary path through an unsupported predicate on amount.direction.
        var category = await CreateCategoryAsync("Field");
        var result = await HandleAsync(
            NewRuleId(),
            category.CategoryId,
            [new ClassificationRuleConditionInput(
                0,
                ClassificationRuleFieldKey.AmountDirection,
                ClassificationRulePredicateKind.StartsWith,
                ValueText: "inflow")]);
        Assert.Equal(RuleVocabularyErrors.PredicateNotAllowed, result.ErrorCode);
        await AssertNoRuleMutationAsync();
    }

    [Fact]
    public async Task Unknown_predicate_on_text_field_returns_stable_error()
    {
        var category = await CreateCategoryAsync("Pred");
        var result = await HandleAsync(
            NewRuleId(),
            category.CategoryId,
            [new ClassificationRuleConditionInput(
                0,
                ClassificationRuleFieldKey.AccountId,
                ClassificationRulePredicateKind.BetweenInclusive,
                ValueMinorMin: 1,
                ValueMinorMax: 2)]);
        Assert.Equal(RuleVocabularyErrors.PredicateNotAllowed, result.ErrorCode);
        await AssertNoRuleMutationAsync();
    }

    [Fact]
    public async Task Duplicate_ordinal_returns_stable_error()
    {
        var category = await CreateCategoryAsync("Ord");
        var result = await HandleAsync(
            NewRuleId(),
            category.CategoryId,
            [
                TextCondition(0, ClassificationRuleFieldKey.AccountId, ClassificationRulePredicateKind.Equals, "a"),
                TextCondition(0, ClassificationRuleFieldKey.AccountId, ClassificationRulePredicateKind.Equals, "b")
            ]);
        Assert.Equal(RuleVocabularyErrors.DuplicateOrdinal, result.ErrorCode);
        await AssertNoRuleMutationAsync();
    }

    [Fact]
    public async Task Semantic_duplicate_conditions_return_stable_error_excluding_ordinal()
    {
        var category = await CreateCategoryAsync("Dup");
        var result = await HandleAsync(
            NewRuleId(),
            category.CategoryId,
            [
                TextCondition(0, ClassificationRuleFieldKey.AccountId, ClassificationRulePredicateKind.Equals, "same"),
                TextCondition(1, ClassificationRuleFieldKey.AccountId, ClassificationRulePredicateKind.Equals, "same")
            ]);
        Assert.Equal(RuleVocabularyErrors.DuplicateCondition, result.ErrorCode);
        await AssertNoRuleMutationAsync();
    }

    [Fact]
    public async Task Invalid_direction_value_returns_stable_error()
    {
        var category = await CreateCategoryAsync("Dir");
        var result = await HandleAsync(
            NewRuleId(),
            category.CategoryId,
            [new ClassificationRuleConditionInput(
                0,
                ClassificationRuleFieldKey.AmountDirection,
                ClassificationRulePredicateKind.Equals,
                ValueText: "sideways")]);
        Assert.Equal(RuleVocabularyErrors.InvalidValue, result.ErrorCode);
        await AssertNoRuleMutationAsync();
    }

    [Fact]
    public async Task Invalid_minor_range_returns_stable_error()
    {
        var category = await CreateCategoryAsync("Minor");
        var result = await HandleAsync(
            NewRuleId(),
            category.CategoryId,
            [new ClassificationRuleConditionInput(
                0,
                ClassificationRuleFieldKey.AmountAbsoluteMinor,
                ClassificationRulePredicateKind.BetweenInclusive,
                ValueMinorMin: 10,
                ValueMinorMax: 1)]);
        Assert.Equal(RuleVocabularyErrors.InvalidMinorRange, result.ErrorCode);
        await AssertNoRuleMutationAsync();
    }

    [Fact]
    public async Task Blank_description_value_returns_stable_error()
    {
        var category = await CreateCategoryAsync("Blank");
        var result = await HandleAsync(
            NewRuleId(),
            category.CategoryId,
            [TextCondition(0, ClassificationRuleFieldKey.DescriptionNormalized, ClassificationRulePredicateKind.Equals, "   ")]);
        Assert.Equal(RuleVocabularyErrors.InvalidValue, result.ErrorCode);
        await AssertNoRuleMutationAsync();
    }

    // ── Category errors ──────────────────────────────────────────────────────

    // FR-CLASSIFY-RULE-LIFECYCLE / AC: category-not-found
    [Fact]
    public async Task Missing_category_returns_not_found_and_creates_no_version()
    {
        var result = await HandleAsync(
            NewRuleId(),
            LedgerId.New().ToString(),
            [TextCondition(0, ClassificationRuleFieldKey.AccountId, ClassificationRulePredicateKind.Equals, "x")]);
        Assert.Equal(ClassifyErrors.NotFound, result.ErrorCode);
        await AssertNoRuleMutationAsync();
    }

    // FR-CLASSIFY-RULE-LIFECYCLE / AC: category-inactive
    [Fact]
    public async Task Archived_category_returns_lifecycle_error_and_creates_no_version()
    {
        var category = await CreateCategoryAsync("Archived");
        await ArchiveCategoryAsync(category.CategoryId);
        var result = await HandleAsync(
            NewRuleId(),
            category.CategoryId,
            [TextCondition(0, ClassificationRuleFieldKey.AccountId, ClassificationRulePredicateKind.Equals, "x")]);
        Assert.Equal(ClassifyErrors.Lifecycle, result.ErrorCode);
        await AssertNoRuleMutationAsync();
    }

    // ── Boundary / envelope ──────────────────────────────────────────────────

    [Fact]
    public async Task Missing_actor_returns_actor_required()
    {
        var category = await CreateCategoryAsync("Actor");
        var result = await command.HandleAsync(
            Request(NewRuleId(), category.CategoryId, [TextCondition(0, ClassificationRuleFieldKey.AccountId, ClassificationRulePredicateKind.Equals, "a")]),
            actor: null,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.ActorRequired, result.ErrorCode);
    }

    [Fact]
    public async Task Missing_idempotency_key_returns_required()
    {
        var category = await CreateCategoryAsync("Idem");
        var result = await command.HandleAsync(
            Request(NewRuleId(), category.CategoryId, [TextCondition(0, ClassificationRuleFieldKey.AccountId, ClassificationRulePredicateKind.Equals, "a")]),
            actor,
            idempotencyKey: null,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.IdempotencyRequired, result.ErrorCode);
    }

    [Fact]
    public async Task Unsupported_contract_version_is_rejected()
    {
        var category = await CreateCategoryAsync("Ver");
        var result = await command.HandleAsync(
            new ClassifyRuleSaveRequest(
                "9.9",
                NewRuleId(),
                null,
                category.CategoryId,
                NormalizationDescriptor.V1.Version,
                [TextCondition(0, ClassificationRuleFieldKey.AccountId, ClassificationRulePredicateKind.Equals, "a")],
                "reason"),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.UnsupportedVersion, result.ErrorCode);
        await AssertNoRuleMutationAsync();
    }

    [Fact]
    public async Task Unsupported_normalization_version_is_rejected()
    {
        var category = await CreateCategoryAsync("Norm");
        var result = await command.HandleAsync(
            new ClassifyRuleSaveRequest(
                ClassifyOperationIds.ContractVersion,
                NewRuleId(),
                null,
                category.CategoryId,
                "normalization_v0",
                [TextCondition(0, ClassificationRuleFieldKey.AccountId, ClassificationRulePredicateKind.Equals, "a")],
                "reason"),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.UnsupportedVersion, result.ErrorCode);
        await AssertNoRuleMutationAsync();
    }

    [Fact]
    public async Task Blank_reason_is_rejected()
    {
        var category = await CreateCategoryAsync("Reason");
        var result = await HandleAsync(
            NewRuleId(),
            category.CategoryId,
            [TextCondition(0, ClassificationRuleFieldKey.AccountId, ClassificationRulePredicateKind.Equals, "a")],
            reason: "  ");
        Assert.Equal(ClassifyErrors.InvalidInput, result.ErrorCode);
        await AssertNoRuleMutationAsync();
    }

    [Fact]
    public async Task Unknown_prior_version_returns_rule_version_not_found()
    {
        var category = await CreateCategoryAsync("PriorMiss");
        var result = await HandleAsync(
            NewRuleId(),
            category.CategoryId,
            [TextCondition(0, ClassificationRuleFieldKey.AccountId, ClassificationRulePredicateKind.Equals, "a")],
            priorVersionId: "does-not-exist");
        Assert.Equal(ClassifyErrors.RuleVersionNotFound, result.ErrorCode);
        await AssertNoRuleMutationAsync();
    }

    // ── Idempotency ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Identical_idempotent_requests_return_stored_draft_result()
    {
        var category = await CreateCategoryAsync("Replay");
        var ruleId = NewRuleId();
        var conditions = new[]
        {
            TextCondition(0, ClassificationRuleFieldKey.DescriptionNormalized, ClassificationRulePredicateKind.Equals, "COSTCO")
        };
        const string key = "idem-replay-1";

        var first = await HandleAsync(ruleId, category.CategoryId, conditions, reason: "replay", key: key);
        var second = await HandleAsync(ruleId, category.CategoryId, conditions, reason: "replay", key: key);

        Assert.True(first.IsSuccess && second.IsSuccess);
        Assert.Equal(first.Value!.RuleVersionId, second.Value!.RuleVersionId);
        Assert.Equal(
            JsonSerializer.Serialize(first.Value),
            JsonSerializer.Serialize(second.Value));
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM rule_version;"));
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM operation_idempotency;"));
    }

    [Fact]
    public async Task Conflicting_idempotency_reuse_preserves_original_row()
    {
        var category = await CreateCategoryAsync("Conflict");
        const string key = "idem-conflict-1";
        var first = await HandleAsync(
            NewRuleId(),
            category.CategoryId,
            [TextCondition(0, ClassificationRuleFieldKey.AccountId, ClassificationRulePredicateKind.Equals, "a")],
            reason: "first",
            key: key);
        Assert.True(first.IsSuccess, first.ErrorCode);

        var conflict = await HandleAsync(
            NewRuleId(),
            category.CategoryId,
            [TextCondition(0, ClassificationRuleFieldKey.AccountId, ClassificationRulePredicateKind.Equals, "b")],
            reason: "second",
            key: key);
        Assert.Equal(ClassifyErrors.IdempotencyConflict, conflict.ErrorCode);
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM rule_version;"));
        Assert.Equal(1L, await CountAsync($"SELECT COUNT(*) FROM rule_version WHERE rule_version_id = '{first.Value!.RuleVersionId}';"));
    }

    [Fact]
    public async Task Replay_succeeds_after_referenced_category_is_archived()
    {
        var category = await CreateCategoryAsync("ReplayArch");
        var ruleId = NewRuleId();
        var conditions = new[]
        {
            TextCondition(0, ClassificationRuleFieldKey.AccountId, ClassificationRulePredicateKind.Equals, "stable")
        };
        const string key = "idem-replay-arch";
        var first = await HandleAsync(ruleId, category.CategoryId, conditions, reason: "r", key: key);
        Assert.True(first.IsSuccess, first.ErrorCode);
        await ArchiveCategoryAsync(category.CategoryId);

        var replay = await HandleAsync(ruleId, category.CategoryId, conditions, reason: "r", key: key);
        Assert.True(replay.IsSuccess, replay.ErrorCode);
        Assert.Equal(first.Value!.RuleVersionId, replay.Value!.RuleVersionId);
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM rule_version;"));
    }

    // Description normalization is applied before fingerprint (equivalent casing shares key)
    [Fact]
    public async Task Equivalent_normalized_description_conditions_share_idempotency_fingerprint()
    {
        var category = await CreateCategoryAsync("NormIdem");
        var ruleId = NewRuleId();
        const string key = "idem-norm-1";
        var first = await HandleAsync(
            ruleId,
            category.CategoryId,
            [TextCondition(0, ClassificationRuleFieldKey.DescriptionNormalized, ClassificationRulePredicateKind.Equals, "Whole   Foods")],
            reason: "n",
            key: key);
        var second = await HandleAsync(
            ruleId,
            category.CategoryId,
            [TextCondition(0, ClassificationRuleFieldKey.DescriptionNormalized, ClassificationRulePredicateKind.Equals, "whole foods")],
            reason: "n",
            key: key);

        Assert.True(first.IsSuccess && second.IsSuccess);
        Assert.Equal(first.Value!.RuleVersionId, second.Value!.RuleVersionId);
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM rule_version;"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Task<CommandResult<ClassifyRuleSaveResult>> HandleAsync(
        string ruleId,
        string categoryId,
        IReadOnlyList<ClassificationRuleConditionInput> conditions,
        string reason = "test reason",
        string? priorVersionId = null,
        string? key = null) =>
        command.HandleAsync(
            Request(ruleId, categoryId, conditions, reason, priorVersionId),
            actor,
            key ?? NextKey(),
            CancellationToken.None);

    private static ClassifyRuleSaveRequest Request(
        string ruleId,
        string categoryId,
        IReadOnlyList<ClassificationRuleConditionInput> conditions,
        string reason = "test reason",
        string? priorVersionId = null) =>
        new(
            ClassifyOperationIds.ContractVersion,
            ruleId,
            priorVersionId,
            categoryId,
            NormalizationDescriptor.V1.Version,
            conditions,
            reason);

    private static ClassificationRuleConditionInput TextCondition(
        int ordinal,
        ClassificationRuleFieldKey field,
        ClassificationRulePredicateKind predicate,
        string value) =>
        new(ordinal, field, predicate, ValueText: value);

    private static ClassificationRuleConditionInput DirectionCondition(
        int ordinal,
        ClassificationAmountDirectionValue direction) =>
        new(ordinal, ClassificationRuleFieldKey.AmountDirection, ClassificationRulePredicateKind.Equals, EnumValue: direction);

    private static ClassificationRuleConditionInput MinorEqualsCondition(int ordinal, long minor) =>
        new(ordinal, ClassificationRuleFieldKey.AmountAbsoluteMinor, ClassificationRulePredicateKind.Equals, ValueMinorMin: minor);

    private string NewRuleId() => $"rule-{Interlocked.Increment(ref ruleSeq):D4}-{Guid.NewGuid():N}"[..32];

    private string NextKey() => $"save-key-{Interlocked.Increment(ref keySeq)}-{Guid.NewGuid():N}";

    private async Task AssertNoRuleMutationAsync()
    {
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM classification_rule;"));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM rule_version;"));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM rule_condition;"));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM active_rule_set;"));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM operation_idempotency;"));
    }

    private async Task<ClassifyActiveRuleSetPointer?> GetActivePointerAsync()
    {
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        return await ruleStore.GetActiveRuleSetAsync(connection, null, CancellationToken.None);
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private Task<CategoryDetail> CreateCategoryAsync(string name) =>
        ExecuteSuccessAsync(
            "ledger.category.create",
            new CreateCategoryInput(name),
            NextKey(),
            LedgerJsonContext.Default.CreateCategoryInput,
            LedgerJsonContext.Default.CategoryDetail);

    private async Task ArchiveCategoryAsync(string categoryId) =>
        _ = await ExecuteSuccessAsync(
            "ledger.category.archive",
            new ArchiveCategoryInput(categoryId, "rule-save-test"),
            NextKey(),
            LedgerJsonContext.Default.ArchiveCategoryInput,
            LedgerJsonContext.Default.CategoryLifecycleResult);

    private async Task<TResult> ExecuteSuccessAsync<TInput, TResult>(
        string operationId,
        TInput input,
        string? idempotencyKey,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TInput> inputType,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultType)
    {
        var descriptor = registry.Find(operationId)
            ?? throw new InvalidOperationException($"Missing operation {operationId}");
        var inputElement = JsonSerializer.SerializeToElement(input, inputType);
        var request = new RequestEnvelope("1.0", actor, inputElement, idempotencyKey);
        var requestJson = JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var arguments = descriptor.CliPath
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Concat(["--input", "-"])
            .ToArray();
        var processResult = await process.RunAsync(arguments, requestJson, CancellationToken.None);
        var envelope = JsonSerializer.Deserialize(processResult.Stdout, LedgerJsonContext.Default.ResultEnvelope)
            ?? throw new InvalidOperationException("No result envelope");
        Assert.Equal(0, processResult.ExitCode);
        Assert.Equal("success", envelope.Outcome);
        return JsonSerializer.Deserialize(envelope.Result!.Value, resultType)
            ?? throw new InvalidOperationException("No typed result");
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
