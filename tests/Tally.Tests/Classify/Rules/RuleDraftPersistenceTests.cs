using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Classify.Rules;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Categories;
using Tally.Domain.Classify.Normalization;
using Tally.Domain.Classify.Rules;
using Tally.Features.Classify.Contract;
using Tally.Features.Classify.Rules.Save;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Rules;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Classify.Rules;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-RULE-DRAFT-SAVE / DD-CLASSIFY-STATE-STORE / bd-1qb6
/// Immutability, origin attribution, and no-activation invariants for draft persistence.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class RuleDraftPersistenceTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-classify-rule-draft-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("human", "owner", "run-draft");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private ClassifyStateStore store = null!;
    private ClassificationRuleStore ruleStore = null!;
    private SaveClassificationRuleCommand command = null!;
    private int keySeq;

    public async Task InitializeAsync()
    {
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        registry = OperationRegistry.Create();
        process = new TallyProcess(registry, LedgerServices.Create(database));
        ledger = new LedgerContractClient(registry, process);
        var classify = await ClassifyStateExtensions.CreateStateAsync(root, CancellationToken.None);
        store = classify.Store;
        ruleStore = new ClassificationRuleStore();
        command = new SaveClassificationRuleCommand(store, ruleStore, ledger, classify.Idempotency);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    // DD-CLASSIFY-STATE-STORE / AC: immutable draft version
    [Fact]
    public async Task Draft_rule_version_and_conditions_reject_update_and_delete()
    {
        var versionId = await SaveDraftAsync();

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, $"UPDATE rule_version SET reason = 'changed' WHERE rule_version_id = '{versionId}';"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, $"DELETE FROM rule_version WHERE rule_version_id = '{versionId}';"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, $"UPDATE rule_condition SET value_text = 'x' WHERE rule_version_id = '{versionId}';"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, $"DELETE FROM rule_condition WHERE rule_version_id = '{versionId}';"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "UPDATE classification_rule SET created_by = 'other';"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "DELETE FROM classification_rule;"));
    }

    // FR-CLASSIFY-RULE-LIFECYCLE / AC: rule_origin=owner_authored
    [Fact]
    public async Task Draft_records_owner_authored_origin_and_never_broad_apply()
    {
        var versionId = await SaveDraftAsync(reason: "deliberate owner rule");

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var version = await ruleStore.GetRuleVersionAsync(connection, null, versionId, CancellationToken.None);
        Assert.NotNull(version);
        Assert.Equal(ClassificationRuleStore.OriginOwnerAuthored, version!.RuleOrigin);
        Assert.Equal(ClassificationRuleStore.LifecycleDraft, version.LifecycleState);
        Assert.Equal(0, version.BroadApplyAllowed);
        Assert.Null(version.SourceFeedbackId);
        Assert.Null(version.ValidationRunId);
        Assert.Equal("deliberate owner rule", version.Reason);
        Assert.StartsWith("human:owner", version.CreatedBy, StringComparison.Ordinal);
    }

    // Scope hash is stable for equivalent condition order
    [Fact]
    public async Task Scope_hash_is_64_hex_and_matches_domain_computation()
    {
        var versionId = await SaveDraftAsync(
            conditions:
            [
                new ClassificationRuleConditionInput(
                    1,
                    ClassificationRuleFieldKey.DescriptionNormalized,
                    ClassificationRulePredicateKind.ContainsTokenSequence,
                    ValueText: "coffee shop"),
                new ClassificationRuleConditionInput(
                    0,
                    ClassificationRuleFieldKey.AmountDirection,
                    ClassificationRulePredicateKind.Equals,
                    EnumValue: ClassificationAmountDirectionValue.Outflow)
            ]);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var version = await ruleStore.GetRuleVersionAsync(connection, null, versionId, CancellationToken.None);
        var conditions = await ruleStore.ListConditionsAsync(connection, null, versionId, CancellationToken.None);
        Assert.Equal(64, version!.ScopeHash.Length);
        Assert.Equal(ClassifyContractMapper.ComputeScopeHash(conditions), version.ScopeHash);
        Assert.All(version.ScopeHash, ch => Assert.True(Uri.IsHexDigit(ch)));
    }

    // No generic upsert — second save appends a new version
    [Fact]
    public async Task Second_save_for_same_rule_appends_new_version_without_in_place_update()
    {
        var category = await CreateCategoryAsync("Append");
        var ruleId = "rule-append-1";
        var first = await command.HandleAsync(
            Request(ruleId, category.CategoryId, [AccountEquals(0, "a")], "first"),
            actor,
            NextKey(),
            CancellationToken.None);
        var second = await command.HandleAsync(
            Request(ruleId, category.CategoryId, [AccountEquals(0, "b")], "second"),
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.True(first.IsSuccess && second.IsSuccess);
        Assert.NotEqual(first.Value!.RuleVersionId, second.Value!.RuleVersionId);
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM classification_rule;"));
        Assert.Equal(2L, await CountAsync("SELECT COUNT(*) FROM rule_version;"));

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var v1 = await ruleStore.GetRuleVersionAsync(connection, null, first.Value.RuleVersionId, CancellationToken.None);
        Assert.Equal("first", v1!.Reason);
        Assert.Equal("a", (await ruleStore.ListConditionsAsync(connection, null, first.Value.RuleVersionId, CancellationToken.None))[0].ValueText);
    }

    // Store rejects non-draft insert path (defensive API contract)
    [Fact]
    public async Task Store_rejects_non_draft_lifecycle_and_broad_apply_on_insert()
    {
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var tx = store.BeginImmediate(connection);
        await ruleStore.InsertRuleAsync(
            connection,
            tx,
            new ClassifyRuleRow("r-def", "2026-07-31T00:00:00.0000000Z", "human:owner"),
            CancellationToken.None);

        var condition = RuleCondition.Create(0, ClassificationRuleVocabulary.AccountId, ClassificationRuleVocabulary.EqualsPredicate, valueText: "x");
        var activeRow = new ClassifyRuleVersionRow(
            "rv-active",
            "r-def",
            null,
            NormalizationDescriptor.V1.Version,
            "cat",
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("scope"u8.ToArray())),
            ClassificationRuleStore.OriginOwnerAuthored,
            null,
            "nope",
            "active",
            0,
            null,
            "2026-07-31T00:00:00.0000000Z",
            "human:owner");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ruleStore.InsertDraftVersionAsync(connection, tx, activeRow, [condition], CancellationToken.None));

        var broadRow = activeRow with { LifecycleState = ClassificationRuleStore.LifecycleDraft, BroadApplyAllowed = 1, RuleVersionId = "rv-broad" };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ruleStore.InsertDraftVersionAsync(connection, tx, broadRow, [condition], CancellationToken.None));
    }

    // Idempotency terminal result is immutable
    [Fact]
    public async Task Idempotency_row_rejects_update_and_delete()
    {
        _ = await SaveDraftAsync();
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "UPDATE operation_idempotency SET terminal_result = 'x';"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteAsync(connection, "DELETE FROM operation_idempotency;"));
    }

    // Active pointer remains null across many drafts
    [Fact]
    public async Task Many_drafts_leave_active_rule_set_empty()
    {
        var category = await CreateCategoryAsync("Many");
        for (var i = 0; i < 3; i++)
        {
            var result = await command.HandleAsync(
                Request($"rule-many-{i}", category.CategoryId, [AccountEquals(0, $"a{i}")], $"r{i}"),
                actor,
                NextKey(),
                CancellationToken.None);
            Assert.True(result.IsSuccess, result.ErrorCode);
        }

        Assert.Equal(3L, await CountAsync("SELECT COUNT(*) FROM rule_version WHERE lifecycle_state = 'draft';"));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM active_rule_set;"));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM rule_set_member;"));
    }

    // Mapper pure helpers
    [Fact]
    public void Mapper_field_and_predicate_round_trip_and_scope_hash_are_stable()
    {
        Assert.Equal(
            ClassificationRuleVocabulary.DescriptionNormalized,
            ClassifyContractMapper.ToFieldKey(ClassificationRuleFieldKey.DescriptionNormalized));
        Assert.Equal(
            ClassificationRulePredicateKind.ContainsTokenSequence,
            ClassifyContractMapper.ParsePredicateKind(ClassificationRuleVocabulary.ContainsTokenSequencePredicate));

        var left = RuleCondition.Create(0, ClassificationRuleVocabulary.AccountId, ClassificationRuleVocabulary.EqualsPredicate, valueText: "a");
        var sameSemantic = RuleCondition.Create(0, ClassificationRuleVocabulary.AccountId, ClassificationRuleVocabulary.EqualsPredicate, valueText: "a");
        var differentValue = RuleCondition.Create(0, ClassificationRuleVocabulary.AccountId, ClassificationRuleVocabulary.EqualsPredicate, valueText: "b");
        Assert.Equal(ClassifyContractMapper.ComputeScopeHash([left]), ClassifyContractMapper.ComputeScopeHash([sameSemantic]));
        Assert.NotEqual(ClassifyContractMapper.ComputeScopeHash([left]), ClassifyContractMapper.ComputeScopeHash([differentValue]));
        Assert.True(left.IsSemanticallyEqualTo(RuleCondition.Create(9, ClassificationRuleVocabulary.AccountId, ClassificationRuleVocabulary.EqualsPredicate, valueText: "a")));
        Assert.True(ClassifyContractMapper.TryNormalizeReason(" ok ", out var reason));
        Assert.Equal("ok", reason);
        Assert.False(ClassifyContractMapper.TryNormalizeReason("", out _));
        Assert.False(ClassifyContractMapper.TryNormalizeReason(new string('x', 1025), out _));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<string> SaveDraftAsync(
        string reason = "persist",
        IReadOnlyList<ClassificationRuleConditionInput>? conditions = null)
    {
        var category = await CreateCategoryAsync("DraftCat-" + Guid.NewGuid().ToString("N")[..8]);
        var result = await command.HandleAsync(
            Request(
                "rule-" + Guid.NewGuid().ToString("N")[..12],
                category.CategoryId,
                conditions ?? [AccountEquals(0, "acct")],
                reason),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!.RuleVersionId;
    }

    private static ClassifyRuleSaveRequest Request(
        string ruleId,
        string categoryId,
        IReadOnlyList<ClassificationRuleConditionInput> conditions,
        string reason) =>
        new(
            ClassifyOperationIds.ContractVersion,
            ruleId,
            null,
            categoryId,
            NormalizationDescriptor.V1.Version,
            conditions,
            reason);

    private static ClassificationRuleConditionInput AccountEquals(int ordinal, string value) =>
        new(ordinal, ClassificationRuleFieldKey.AccountId, ClassificationRulePredicateKind.Equals, ValueText: value);

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private Task<CategoryDetail> CreateCategoryAsync(string name) =>
        ExecuteSuccessAsync(
            "ledger.category.create",
            new CreateCategoryInput(name),
            NextKey(),
            LedgerJsonContext.Default.CreateCategoryInput,
            LedgerJsonContext.Default.CategoryDetail);

    private async Task<TResult> ExecuteSuccessAsync<TInput, TResult>(
        string operationId,
        TInput input,
        string? idempotencyKey,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TInput> inputType,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultType)
    {
        var descriptor = registry.Find(operationId)
            ?? throw new InvalidOperationException($"Missing operation {operationId}");
        var inputElement = System.Text.Json.JsonSerializer.SerializeToElement(input, inputType);
        var request = new RequestEnvelope("1.0", actor, inputElement, idempotencyKey);
        var requestJson = System.Text.Json.JsonSerializer.Serialize(request, LedgerJsonContext.Default.RequestEnvelope);
        var arguments = descriptor.CliPath
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Concat(["--input", "-"])
            .ToArray();
        var processResult = await process.RunAsync(arguments, requestJson, CancellationToken.None);
        var envelope = System.Text.Json.JsonSerializer.Deserialize(processResult.Stdout, LedgerJsonContext.Default.ResultEnvelope)
            ?? throw new InvalidOperationException("No result envelope");
        Assert.Equal(0, processResult.ExitCode);
        return System.Text.Json.JsonSerializer.Deserialize(envelope.Result!.Value, resultType)
            ?? throw new InvalidOperationException("No typed result");
    }

    private string NextKey() => $"draft-key-{Interlocked.Increment(ref keySeq)}-{Guid.NewGuid():N}";
}
