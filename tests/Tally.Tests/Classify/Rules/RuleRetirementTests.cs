using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Classify.Operations;
using Tally.Contracts.Classify.Rules;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Classify.Normalization;
using Tally.Domain.Classify.Rules;
using Tally.Domain.Ledger;
using Tally.Features.Classify.Contract;
using Tally.Features.Classify.Rules.Activate;
using Tally.Features.Classify.Rules.Retire;
using Tally.Features.Classify.Rules.Save;
using Tally.Features.Classify.Rules.Validate;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Rules;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Classify.Rules;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-RULE-ACTIVATION-LIFECYCLE / FR-CLASSIFY-RULE-LIFECYCLE / bd-3e6o
/// Retirement, replacement, and immutable history retention.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class RuleRetirementTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-rule-retire-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("automation", "rule-retire", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private ClassifyStateStore store = null!;
    private ClassificationRuleStore ruleStore = null!;
    private ClassificationValidationStore validationStore = null!;
    private RuleSetStore ruleSetStore = null!;
    private SaveClassificationRuleCommand save = null!;
    private ValidateClassificationRuleCommand validate = null!;
    private ActivateClassificationRuleCommand activate = null!;
    private RetireClassificationRuleCommand retire = null!;
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
        var services = await ClassifyRuleExtensions.CreateServicesAsync(root, ledger, cancellationToken: CancellationToken.None);
        store = services.State.Store;
        ruleStore = services.RuleStore;
        validationStore = services.ValidationStore;
        ruleSetStore = services.RuleSetStore;
        save = services.Save;
        activate = services.Activate;
        retire = services.Retire;
        validate = new ValidateClassificationRuleCommand(
            store, ruleStore, validationStore, ClassifyCorpusExtensions.CreateReader(), ledger, services.State.Idempotency);
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

    // ── Retirement success ───────────────────────────────────────────────────

    [Fact]
    public async Task Retire_active_member_creates_successor_set_without_that_rule()
    {
        var category = await CreateCategoryAsync("Keep");
        var other = await CreateCategoryAsync("Drop");
        var keep = await SaveDraftAsync(category.CategoryId, "keep me", ruleId: "rule-keep");
        var drop = await SaveDraftAsync(other.CategoryId, "drop me", ruleId: "rule-drop");
        var path = await WriteBoundCorpusAsync([
            ("keep me", category.CategoryId, "suggestion"),
            ("drop me", other.CategoryId, "suggestion")
        ]);
        var validation = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [keep, drop], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(validation.IsSuccess && validation.Value!.ActivationEligible, validation.ErrorCode);
        var activated = await activate.HandleAsync(
            new ClassifyRuleActivateRequest(
                ClassifyOperationIds.ContractVersion, validation.Value.ValidationId, false, "activate both"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(activated.IsSuccess, activated.ErrorCode);

        var result = await retire.HandleAsync(
            new ClassifyRuleRetireRequest(ClassifyOperationIds.ContractVersion, drop, "retire drop"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(drop, result.Value!.RetiredRuleVersionId);
        Assert.NotEqual(activated.Value!.RuleSetVersionId, result.Value.SuccessorRuleSetVersionId);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var pointer = await ruleSetStore.GetActiveRuleSetAsync(connection, null, CancellationToken.None);
        Assert.Equal(result.Value.SuccessorRuleSetVersionId, pointer!.RuleSetVersionId);
        var members = await ruleSetStore.ListMemberRuleVersionIdsAsync(
            connection, null, pointer.RuleSetVersionId, CancellationToken.None);
        Assert.Equal([keep], members);
        Assert.DoesNotContain(drop, members);

        // Prior set retains full membership history.
        var priorMembers = await ruleSetStore.ListMemberRuleVersionIdsAsync(
            connection, null, activated.Value.RuleSetVersionId, CancellationToken.None);
        Assert.Contains(drop, priorMembers);
        Assert.Contains(keep, priorMembers);
        Assert.Equal(2L, await ruleSetStore.CountRuleSetVersionsAsync(connection, null, CancellationToken.None));
    }

    [Fact]
    public async Task Retire_last_member_leaves_empty_successor_and_retains_history()
    {
        var category = await CreateCategoryAsync("Last");
        var versionId = await SaveDraftAsync(category.CategoryId, "only one");
        var validationId = await ValidateEligibleAsync(versionId, category.CategoryId, "only one");
        var activated = await activate.HandleAsync(
            new ClassifyRuleActivateRequest(ClassifyOperationIds.ContractVersion, validationId, false, "solo"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(activated.IsSuccess, activated.ErrorCode);

        var result = await retire.HandleAsync(
            new ClassifyRuleRetireRequest(ClassifyOperationIds.ContractVersion, versionId, "retire last"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var members = await ruleSetStore.ListMemberRuleVersionIdsAsync(
            connection, null, result.Value!.SuccessorRuleSetVersionId, CancellationToken.None);
        Assert.Empty(members);
        var retained = await ruleStore.GetRuleVersionAsync(connection, null, versionId, CancellationToken.None);
        Assert.NotNull(retained);
        var priorMembers = await ruleSetStore.ListMemberRuleVersionIdsAsync(
            connection, null, activated.Value!.RuleSetVersionId, CancellationToken.None);
        Assert.Equal([versionId], priorMembers);
    }

    [Fact]
    public async Task Retire_records_attributable_lifecycle_events()
    {
        var category = await CreateCategoryAsync("Events");
        var versionId = await SaveDraftAsync(category.CategoryId, "evented");
        var validationId = await ValidateEligibleAsync(versionId, category.CategoryId, "evented");
        _ = await activate.HandleAsync(
            new ClassifyRuleActivateRequest(ClassifyOperationIds.ContractVersion, validationId, false, "activate"),
            actor, NextKey(), CancellationToken.None);

        var result = await retire.HandleAsync(
            new ClassifyRuleRetireRequest(ClassifyOperationIds.ContractVersion, versionId, "retire with events"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var events = await ruleSetStore.ListLifecycleEventsForSubjectAsync(
            connection, null, versionId, CancellationToken.None);
        Assert.Contains(events, e => e.ResultingState == RuleLifecyclePolicy.StateRetired);
        Assert.All(events, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Actor));
            Assert.False(string.IsNullOrWhiteSpace(e.Reason));
            Assert.False(string.IsNullOrWhiteSpace(e.OccurredAt));
        });
    }

    [Fact]
    public async Task Retire_never_mutates_retired_rule_version_row()
    {
        var category = await CreateCategoryAsync("ImmutableRetire");
        var versionId = await SaveDraftAsync(category.CategoryId, "immutable retire");
        var validationId = await ValidateEligibleAsync(versionId, category.CategoryId, "immutable retire");
        _ = await activate.HandleAsync(
            new ClassifyRuleActivateRequest(ClassifyOperationIds.ContractVersion, validationId, false, "activate"),
            actor, NextKey(), CancellationToken.None);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var before = await ruleStore.GetRuleVersionAsync(connection, null, versionId, CancellationToken.None);
        var result = await retire.HandleAsync(
            new ClassifyRuleRetireRequest(ClassifyOperationIds.ContractVersion, versionId, "keep bytes"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var after = await ruleStore.GetRuleVersionAsync(connection, null, versionId, CancellationToken.None);
        Assert.Equal(before!.ScopeHash, after!.ScopeHash);
        Assert.Equal(before.CategoryId, after.CategoryId);
        Assert.Equal(before.Reason, after.Reason);
        Assert.Equal(before.LifecycleState, after.LifecycleState);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(before.CreatedBy, after.CreatedBy);
    }

    // ── Failure / fail-closed ────────────────────────────────────────────────

    [Fact]
    public async Task Retire_non_member_fails_closed_without_pointer_change()
    {
        var category = await CreateCategoryAsync("Member");
        var versionId = await SaveDraftAsync(category.CategoryId, "active one");
        var other = await SaveDraftAsync(category.CategoryId, "not active", ruleId: "rule-other");
        var validationId = await ValidateEligibleAsync(versionId, category.CategoryId, "active one");
        var activated = await activate.HandleAsync(
            new ClassifyRuleActivateRequest(ClassifyOperationIds.ContractVersion, validationId, false, "activate"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(activated.IsSuccess, activated.ErrorCode);

        var result = await retire.HandleAsync(
            new ClassifyRuleRetireRequest(ClassifyOperationIds.ContractVersion, other, "not a member"),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.Lifecycle, result.ErrorCode);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var pointer = await ruleSetStore.GetActiveRuleSetAsync(connection, null, CancellationToken.None);
        Assert.Equal(activated.Value!.RuleSetVersionId, pointer!.RuleSetVersionId);
        Assert.Equal(1L, await ruleSetStore.CountRuleSetVersionsAsync(connection, null, CancellationToken.None));
    }

    [Fact]
    public async Task Retire_without_active_set_fails_lifecycle()
    {
        var category = await CreateCategoryAsync("NoActive");
        var versionId = await SaveDraftAsync(category.CategoryId, "draft only");
        var result = await retire.HandleAsync(
            new ClassifyRuleRetireRequest(ClassifyOperationIds.ContractVersion, versionId, "no active"),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.Lifecycle, result.ErrorCode);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM rule_set_version;"));
    }

    [Fact]
    public async Task Retire_unknown_rule_version_is_not_found()
    {
        var result = await retire.HandleAsync(
            new ClassifyRuleRetireRequest(ClassifyOperationIds.ContractVersion, "missing-version", "missing"),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.RuleVersionNotFound, result.ErrorCode);
    }

    [Fact]
    public async Task Retire_remaining_member_with_archived_category_fails_closed()
    {
        var catKeep = await CreateCategoryAsync("KeepArch");
        var catDrop = await CreateCategoryAsync("DropArch");
        var keep = await SaveDraftAsync(catKeep.CategoryId, "keep arch", ruleId: "rule-keep-arch");
        var drop = await SaveDraftAsync(catDrop.CategoryId, "drop arch", ruleId: "rule-drop-arch");
        var path = await WriteBoundCorpusAsync([
            ("keep arch", catKeep.CategoryId, "suggestion"),
            ("drop arch", catDrop.CategoryId, "suggestion")
        ]);
        var validation = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [keep, drop], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(validation.IsSuccess && validation.Value!.ActivationEligible, validation.ErrorCode);
        var activated = await activate.HandleAsync(
            new ClassifyRuleActivateRequest(
                ClassifyOperationIds.ContractVersion, validation.Value.ValidationId, false, "both"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(activated.IsSuccess, activated.ErrorCode);

        await ArchiveCategoryAsync(catKeep.CategoryId);
        var result = await retire.HandleAsync(
            new ClassifyRuleRetireRequest(ClassifyOperationIds.ContractVersion, drop, "archive remaining"),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.Lifecycle, result.ErrorCode);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var pointer = await ruleSetStore.GetActiveRuleSetAsync(connection, null, CancellationToken.None);
        Assert.Equal(activated.Value!.RuleSetVersionId, pointer!.RuleSetVersionId);
    }

    // ── Replacement via activate after retire ────────────────────────────────

    [Fact]
    public async Task Replace_via_retire_then_activate_successor_retains_all_history()
    {
        var category = await CreateCategoryAsync("ReplaceChain");
        var v1 = await SaveDraftAsync(category.CategoryId, "original", ruleId: "rule-orig");
        var validation1 = await ValidateEligibleAsync(v1, category.CategoryId, "original");
        var first = await activate.HandleAsync(
            new ClassifyRuleActivateRequest(ClassifyOperationIds.ContractVersion, validation1, false, "first"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(first.IsSuccess, first.ErrorCode);

        var retired = await retire.HandleAsync(
            new ClassifyRuleRetireRequest(ClassifyOperationIds.ContractVersion, v1, "retire original"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(retired.IsSuccess, retired.ErrorCode);

        var v2 = await SaveDraftAsync(category.CategoryId, "successor", ruleId: "rule-succ", priorVersionId: v1);
        var validation2 = await ValidateEligibleAsync(v2, category.CategoryId, "successor");
        var second = await activate.HandleAsync(
            new ClassifyRuleActivateRequest(ClassifyOperationIds.ContractVersion, validation2, false, "activate successor"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(second.IsSuccess, second.ErrorCode);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        Assert.Equal(3L, await ruleSetStore.CountRuleSetVersionsAsync(connection, null, CancellationToken.None));
        var v1Row = await ruleStore.GetRuleVersionAsync(connection, null, v1, CancellationToken.None);
        var v2Row = await ruleStore.GetRuleVersionAsync(connection, null, v2, CancellationToken.None);
        Assert.NotNull(v1Row);
        Assert.NotNull(v2Row);
        Assert.Equal(v1, v2Row!.PriorVersionId);
        var pointer = await ruleSetStore.GetActiveRuleSetAsync(connection, null, CancellationToken.None);
        Assert.Equal(second.Value!.RuleSetVersionId, pointer!.RuleSetVersionId);
        var activeMembers = await ruleSetStore.ListMemberRuleVersionIdsAsync(
            connection, null, pointer.RuleSetVersionId, CancellationToken.None);
        Assert.Equal([v2], activeMembers);
    }

    [Fact]
    public async Task Retire_idempotent_replay_is_stable()
    {
        var category = await CreateCategoryAsync("IdemRetire");
        var versionId = await SaveDraftAsync(category.CategoryId, "idem retire");
        var validationId = await ValidateEligibleAsync(versionId, category.CategoryId, "idem retire");
        _ = await activate.HandleAsync(
            new ClassifyRuleActivateRequest(ClassifyOperationIds.ContractVersion, validationId, false, "activate"),
            actor, NextKey(), CancellationToken.None);
        const string key = "retire-idem-1";
        var first = await retire.HandleAsync(
            new ClassifyRuleRetireRequest(ClassifyOperationIds.ContractVersion, versionId, "idem"),
            actor, key, CancellationToken.None);
        var second = await retire.HandleAsync(
            new ClassifyRuleRetireRequest(ClassifyOperationIds.ContractVersion, versionId, "idem"),
            actor, key, CancellationToken.None);
        Assert.True(first.IsSuccess && second.IsSuccess);
        Assert.Equal(first.Value!.SuccessorRuleSetVersionId, second.Value!.SuccessorRuleSetVersionId);
        Assert.Equal(2L, await CountAsync("SELECT COUNT(*) FROM rule_set_version;"));
    }

    [Fact]
    public async Task Retire_requires_actor_idempotency_and_reason()
    {
        var category = await CreateCategoryAsync("EnvRetire");
        var versionId = await SaveDraftAsync(category.CategoryId, "env retire");
        var validationId = await ValidateEligibleAsync(versionId, category.CategoryId, "env retire");
        _ = await activate.HandleAsync(
            new ClassifyRuleActivateRequest(ClassifyOperationIds.ContractVersion, validationId, false, "activate"),
            actor, NextKey(), CancellationToken.None);

        var noActor = await retire.HandleAsync(
            new ClassifyRuleRetireRequest(ClassifyOperationIds.ContractVersion, versionId, "x"),
            null, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.ActorRequired, noActor.ErrorCode);
        var noKey = await retire.HandleAsync(
            new ClassifyRuleRetireRequest(ClassifyOperationIds.ContractVersion, versionId, "x"),
            actor, null, CancellationToken.None);
        Assert.Equal(ClassifyErrors.IdempotencyRequired, noKey.ErrorCode);
        var noReason = await retire.HandleAsync(
            new ClassifyRuleRetireRequest(ClassifyOperationIds.ContractVersion, versionId, " "),
            actor, NextKey(), CancellationToken.None);
        Assert.Equal(ClassifyErrors.InvalidInput, noReason.ErrorCode);
    }

    [Fact]
    public async Task Retire_never_mutates_ledger()
    {
        var category = await CreateCategoryAsync("LedgerRetire");
        var versionId = await SaveDraftAsync(category.CategoryId, "ledger retire");
        var validationId = await ValidateEligibleAsync(versionId, category.CategoryId, "ledger retire");
        _ = await activate.HandleAsync(
            new ClassifyRuleActivateRequest(ClassifyOperationIds.ContractVersion, validationId, false, "activate"),
            actor, NextKey(), CancellationToken.None);
        var beforeName = category.Name;
        var result = await retire.HandleAsync(
            new ClassifyRuleRetireRequest(ClassifyOperationIds.ContractVersion, versionId, "no ledger"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        var listed = await ledger.ListClassificationCategoriesAsync("1.0", actor, CancellationToken.None, status: null);
        var after = Assert.Single(listed.Value!.Items, x => x.CategoryId == category.CategoryId);
        Assert.Equal(beforeName, after.Name);
    }

    [Fact]
    public async Task Policy_successor_members_exclude_retired_version_only()
    {
        var successor = RuleLifecyclePolicy.SuccessorMembersAfterRetirement(
            ["rv-a", "rv-b", "rv-c"],
            "rv-b");
        Assert.Equal(new[] { "rv-a", "rv-c" }, successor);
        Assert.Empty(RuleLifecyclePolicy.SuccessorMembersAfterRetirement(["rv-a"], "rv-a"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<string> ValidateEligibleAsync(string versionId, string categoryId, string description)
    {
        var path = await WriteBoundCorpusAsync([(description, categoryId, "suggestion")]);
        var validation = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor, NextKey(), CancellationToken.None);
        Assert.True(validation.IsSuccess, validation.ErrorCode);
        Assert.True(validation.Value!.ActivationEligible);
        return validation.Value.ValidationId;
    }

    private async Task<string> SaveDraftAsync(
        string categoryId,
        string description,
        string? ruleId = null,
        string? priorVersionId = null)
    {
        var result = await save.HandleAsync(
            new ClassifyRuleSaveRequest(
                ClassifyOperationIds.ContractVersion,
                ruleId ?? "rule-" + Guid.NewGuid().ToString("N")[..12],
                priorVersionId,
                categoryId,
                NormalizationDescriptor.V1.Version,
                [
                    new ClassificationRuleConditionInput(
                        0,
                        ClassificationRuleFieldKey.DescriptionNormalized,
                        ClassificationRulePredicateKind.Equals,
                        ValueText: description)
                ],
                "retirement draft"),
            actor, NextKey(), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!.RuleVersionId;
    }

    private async Task<string> WriteBoundCorpusAsync(
        IReadOnlyList<(string Description, string? ExpectedCategory, string ExpectedKind)> rows)
    {
        var created = new List<(string TxId, string Description)>();
        foreach (var row in rows)
        {
            var tx = await RecordAsync(row.Description, "-12.34");
            created.Add((tx.TransactionId, row.Description));
        }

        var page = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation,
            CategoryContractVersions.Current,
            actor,
            CancellationToken.None);
        Assert.True(page.IsSuccess, page.Error?.Code);
        var byTx = page.Value!.ClassificationItems!
            .ToDictionary(i => i.TransactionId, StringComparer.Ordinal);

        var lines = new List<string>();
        for (var i = 0; i < created.Count; i++)
        {
            var (txId, description) = created[i];
            Assert.True(byTx.TryGetValue(txId, out var item));
            Assert.True(ValidateClassificationRuleCommand.TryMapPublicAmount(item, out var direction, out var abs));
            var life = ValidateClassificationRuleCommand.ComputeItemLifecycleFingerprint(item);
            var expected = rows[i];
            lines.Add(CorpusLine(
                i, txId, item.AccountId, description, direction, abs, life,
                expected.ExpectedKind, expected.ExpectedCategory));
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

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private async Task<AccountDetail> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        return await ExecuteSuccessAsync(
            "ledger.account.create",
            new CreateAccountInput("Ret Bank " + unique, "Primary-" + unique, AccountType.Cheque, "****" + unique[..4], "ZAR"),
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

    private async Task ArchiveCategoryAsync(string categoryId) =>
        _ = await ExecuteSuccessAsync(
            "ledger.category.archive",
            new ArchiveCategoryInput(categoryId, "retirement-test"),
            NextKey(),
            LedgerJsonContext.Default.ArchiveCategoryInput,
            LedgerJsonContext.Default.CategoryLifecycleResult);

    private async Task<TransactionDetail> RecordAsync(string description, string amount)
    {
        var digestText = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"))));
        return await ExecuteSuccessAsync(
            "ledger.transaction.record",
            new RecordTransactionInput(
                accountId,
                amount,
                "ZAR",
                "2026-07-15",
                null,
                description,
                null,
                null,
                new RegisterEvidenceInput(
                    EvidenceKind.AgentCapture,
                    digestText,
                    "retire-capture:" + Guid.NewGuid().ToString("N")[..8],
                    null,
                    null)),
            NextKey(),
            LedgerJsonContext.Default.RecordTransactionInput,
            LedgerJsonContext.Default.TransactionDetail);
    }

    private async Task<TResult> ExecuteSuccessAsync<TInput, TResult>(
        string operationId,
        TInput input,
        string? idempotencyKey,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TInput> inputType,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultType)
    {
        var descriptor = registry.Find(operationId)
            ?? throw new InvalidOperationException($"Missing {operationId}");
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
            ?? throw new InvalidOperationException("No envelope");
        Assert.Equal(0, processResult.ExitCode);
        return JsonSerializer.Deserialize(envelope.Result!.Value, resultType)
            ?? throw new InvalidOperationException("No typed result");
    }

    private string NextKey() => $"ret-key-{Interlocked.Increment(ref keySeq)}-{Guid.NewGuid():N}";
}
