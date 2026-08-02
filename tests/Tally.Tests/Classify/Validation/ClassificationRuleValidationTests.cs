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
using Tally.Features.Classify.Rules.Save;
using Tally.Features.Classify.Rules.Validate;
using Tally.Infrastructure.Classify.Corpus;
using Tally.Infrastructure.Classify.Storage;
using Tally.Infrastructure.Classify.Storage.Rules;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Classify.Validation;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-RULE-VALIDATION / FR-CLASSIFY-RULE-VALIDATION / bd-2kpw
/// Fingerprints, accounting, canaries, corpus-unavailable, no activation.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ClassificationRuleValidationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-rule-validate-" + Guid.NewGuid().ToString("N"));
    private readonly SafeActor actor = new("automation", "rule-validate", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private ClassifyStateStore store = null!;
    private ClassificationRuleStore ruleStore = null!;
    private ClassificationValidationStore validationStore = null!;
    private SaveClassificationRuleCommand save = null!;
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
        var classify = await ClassifyStateExtensions.CreateStateAsync(root, CancellationToken.None);
        store = classify.Store;
        ruleStore = new ClassificationRuleStore();
        validationStore = new ClassificationValidationStore();
        save = new SaveClassificationRuleCommand(store, ruleStore, ledger, classify.Idempotency);
        validate = new ValidateClassificationRuleCommand(
            store,
            ruleStore,
            validationStore,
            ClassifyCorpusExtensions.CreateReader(),
            ledger,
            classify.Idempotency);
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

    // ── Success / accounting ─────────────────────────────────────────────────

    [Fact]
    public async Task Valid_candidate_over_matching_corpus_is_activation_eligible()
    {
        var category = await CreateCategoryAsync("Groceries");
        var versionId = await SaveDraftAsync(
            category.CategoryId,
            DescriptionEquals("whole foods"));
        // Description must normalize-equal the rule operand; bind to live LEDGER projection.
        var corpus = await WriteBoundCorpusAsync([
            ("whole foods", "suggestion", category.CategoryId)
        ]);

        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], corpus),
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.True(result.Value!.ActivationEligible);
        Assert.Equal(1, result.Value.TotalRows);
        Assert.Equal(1, result.Value.SuggestionCount);
        Assert.Equal(0, result.Value.IncorrectApplicationCanaries);
        Assert.Equal(64, result.Value.CorpusFingerprint.Length);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.ValidationId));

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var run = await validationStore.GetRunAsync(connection, null, result.Value.ValidationId, CancellationToken.None);
        var report = await validationStore.GetReportAsync(connection, null, result.Value.ValidationId, CancellationToken.None);
        Assert.NotNull(run);
        Assert.NotNull(report);
        Assert.Equal(ClassificationValidationStore.LifecycleCompleted, run!.LifecycleState);
        Assert.Equal(1, report!.AccountedRows);
        Assert.Equal(1, report.TotalRows);
        Assert.Equal(0, report.UnexplainedConflictCount);
        Assert.Equal(0, await validationStore.CountActiveRuleSetAsync(connection, null, CancellationToken.None));
    }

    [Fact]
    public async Task Every_row_accounted_exactly_once_with_mixed_partition()
    {
        var catA = await CreateCategoryAsync("A");
        var catB = await CreateCategoryAsync("B");
        var vA = await SaveDraftAsync(catA.CategoryId, DescriptionEquals("alpha"), ruleId: "rule-a");
        var vB = await SaveDraftAsync(catB.CategoryId, DescriptionEquals("beta"), ruleId: "rule-b");
        var corpus = await WriteBoundCorpusAsync([
            ("alpha", "suggestion", catA.CategoryId),
            ("beta", "suggestion", catB.CategoryId),
            ("none", "no_suggestion", null)
        ]);

        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [vA, vB], corpus),
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(3, result.Value!.TotalRows);
        Assert.Equal(2, result.Value.SuggestionCount);
        Assert.Equal(0, result.Value.ConflictCount);
        Assert.True(result.Value.ActivationEligible);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var report = await validationStore.GetReportAsync(connection, null, result.Value.ValidationId, CancellationToken.None);
        Assert.Equal(3, report!.AccountedRows);
        Assert.Equal(1, report.NoSuggestionCount);
        Assert.Equal(3, report.SuggestionCount + report.NoSuggestionCount + report.ConflictCount + report.StaleCount);
    }

    // ── Fingerprints ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Validation_run_binds_all_required_fingerprints()
    {
        var category = await CreateCategoryAsync("Bind");
        var versionId = await SaveDraftAsync(category.CategoryId, DescriptionEquals("bindme"));
        var corpus = await WriteBoundCorpusAsync([
            ("bindme", "suggestion", category.CategoryId)
        ]);
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], corpus),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var run = await validationStore.GetRunAsync(connection, null, result.Value!.ValidationId, CancellationToken.None);
        Assert.Equal(64, run!.CandidateFingerprint.Length);
        Assert.Equal(64, run.CorpusFingerprint.Length);
        Assert.Equal(64, run.ExpectedOutcomeFingerprint.Length);
        Assert.Equal(64, run.CategoryLifecycleFingerprint.Length);
        Assert.Equal(ClassificationProjectionVersions.ClassificationV1, run.ProjectionContractVersion);
        Assert.Equal(NormalizationDescriptor.V1.Version, run.NormalizationVersion);
        Assert.Equal(ClassificationRuleStore.OriginOwnerAuthored, run.RuleOrigin);
    }

    // ── Canaries ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Incorrect_application_canary_makes_activation_ineligible()
    {
        var category = await CreateCategoryAsync("Wrong");
        var other = await CreateCategoryAsync("Other");
        var versionId = await SaveDraftAsync(category.CategoryId, DescriptionEquals("shop"));
        var corpus = await WriteBoundCorpusAsync([
            // Engine will suggest `category`, but expected is `other` → incorrect application.
            ("shop", "suggestion", other.CategoryId)
        ]);

        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], corpus),
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.False(result.Value!.ActivationEligible);
        Assert.True(result.Value.IncorrectApplicationCanaries >= 1);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var report = await validationStore.GetReportAsync(connection, null, result.Value.ValidationId, CancellationToken.None);
        Assert.True(report!.IncorrectApplicationCanaryCount >= 1);
        Assert.Equal(0, await validationStore.CountActiveRuleSetAsync(connection, null, CancellationToken.None));
    }

    [Fact]
    public async Task Unexplained_conflict_makes_activation_ineligible()
    {
        var catA = await CreateCategoryAsync("CA");
        var catB = await CreateCategoryAsync("CB");
        var vA = await SaveDraftAsync(catA.CategoryId, DescriptionEquals("clash"), ruleId: "rule-ca");
        var vB = await SaveDraftAsync(catB.CategoryId, DescriptionEquals("clash"), ruleId: "rule-cb");
        var corpus = await WriteBoundCorpusAsync([
            // Expected suggestion, engine will conflict → incorrect + unexplained conflict paths.
            ("clash", "suggestion", catA.CategoryId)
        ]);

        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [vA, vB], corpus),
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(1, result.Value!.ConflictCount);
        Assert.False(result.Value.ActivationEligible);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var report = await validationStore.GetReportAsync(connection, null, result.Value.ValidationId, CancellationToken.None);
        Assert.True(report!.UnexplainedConflictCount >= 1 || report.IncorrectApplicationCanaryCount >= 1);
    }

    [Fact]
    public async Task Expected_conflict_is_explained_and_may_remain_eligible_when_correct()
    {
        var catA = await CreateCategoryAsync("XA");
        var catB = await CreateCategoryAsync("XB");
        var vA = await SaveDraftAsync(catA.CategoryId, DescriptionEquals("both"), ruleId: "rule-xa");
        var vB = await SaveDraftAsync(catB.CategoryId, DescriptionEquals("both"), ruleId: "rule-xb");
        var corpus = await WriteBoundCorpusAsync([
            ("both", "conflict", null)
        ]);

        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [vA, vB], corpus),
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(1, result.Value!.ConflictCount);
        Assert.True(result.Value.ActivationEligible);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var report = await validationStore.GetReportAsync(connection, null, result.Value.ValidationId, CancellationToken.None);
        Assert.Equal(0, report!.UnexplainedConflictCount);
        Assert.Equal(0, report.IncorrectApplicationCanaryCount);
    }

    // ── Corpus unavailable / fail closed ─────────────────────────────────────

    [Fact]
    public async Task Missing_corpus_fails_closed_without_active_set_change()
    {
        var category = await CreateCategoryAsync("Miss");
        var versionId = await SaveDraftAsync(category.CategoryId, DescriptionEquals("x"));
        var missing = Path.Combine(root, "does-not-exist.jsonl");

        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], missing),
            actor,
            NextKey(),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(PrivateCorpusErrors.NotFound, result.ErrorCode);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        Assert.Equal(0, await validationStore.CountActiveRuleSetAsync(connection, null, CancellationToken.None));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM validation_run;"));
    }

    [Fact]
    public async Task Blank_corpus_source_fails_closed()
    {
        var category = await CreateCategoryAsync("BlankSrc");
        var versionId = await SaveDraftAsync(category.CategoryId, DescriptionEquals("x"));
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], "  "),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(PrivateCorpusErrors.PathRequired, result.ErrorCode);
    }

    [Fact]
    public async Task Malformed_corpus_fails_closed_without_activation()
    {
        var category = await CreateCategoryAsync("Bad");
        var versionId = await SaveDraftAsync(category.CategoryId, DescriptionEquals("x"));
        var path = Path.Combine(root, "bad.jsonl");
        WriteOwnerFile(path, "{not-json\n");
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(PrivateCorpusErrors.Malformed, result.ErrorCode);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        Assert.Equal(0, await validationStore.CountActiveRuleSetAsync(connection, null, CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_candidate_returns_rule_version_not_found()
    {
        // Bound corpus still required; candidate lookup fails closed before authority.
        var corpus = await WriteBoundCorpusAsync([("x", null, null)]);
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, ["missing-version"], corpus),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.RuleVersionNotFound, result.ErrorCode);
    }

    // ── No activation / no ledger mutation ───────────────────────────────────

    [Fact]
    public async Task Validation_never_creates_active_rule_set_or_rule_set_version()
    {
        var category = await CreateCategoryAsync("NoAct");
        var versionId = await SaveDraftAsync(category.CategoryId, DescriptionEquals("keep"));
        var corpus = await WriteBoundCorpusAsync([
            ("keep", "suggestion", category.CategoryId)
        ]);
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], corpus),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM active_rule_set;"));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM rule_set_version;"));
        Assert.Equal(0L, await CountAsync("SELECT COUNT(*) FROM rule_set_member;"));
    }

    // ── Idempotency ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Identical_idempotent_validation_replays_terminal_result()
    {
        var category = await CreateCategoryAsync("Idem");
        var versionId = await SaveDraftAsync(category.CategoryId, DescriptionEquals("idem"));
        var corpus = await WriteBoundCorpusAsync([
            ("idem", "suggestion", category.CategoryId)
        ]);
        const string key = "validate-idem-1";
        var first = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], corpus),
            actor,
            key,
            CancellationToken.None);
        var second = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], corpus),
            actor,
            key,
            CancellationToken.None);
        Assert.True(first.IsSuccess && second.IsSuccess);
        Assert.Equal(first.Value!.ValidationId, second.Value!.ValidationId);
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM validation_run;"));
    }

    // ── Boundary ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Missing_actor_and_idempotency_are_rejected()
    {
        var category = await CreateCategoryAsync("Env");
        var versionId = await SaveDraftAsync(category.CategoryId, DescriptionEquals("e"));
        var corpus = await WriteBoundCorpusAsync([("e", null, null)]);
        var noActor = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], corpus),
            null,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.ActorRequired, noActor.ErrorCode);
        var noKey = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], corpus),
            actor,
            null,
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.IdempotencyRequired, noKey.ErrorCode);
    }

    [Fact]
    public async Task Empty_candidate_list_is_rejected()
    {
        var corpus = await WriteBoundCorpusAsync([("e", null, null)]);
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [], corpus),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.InvalidInput, result.ErrorCode);
    }

    [Fact]
    public async Task Unsupported_contract_version_is_rejected()
    {
        var category = await CreateCategoryAsync("Ver");
        var versionId = await SaveDraftAsync(category.CategoryId, DescriptionEquals("v"));
        var corpus = await WriteBoundCorpusAsync([("v", null, null)]);
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest("9.9", [versionId], corpus),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.Equal(ClassifyErrors.UnsupportedVersion, result.ErrorCode);
    }

    [Fact]
    public async Task No_suggestion_expected_with_matching_rule_is_incorrect_canary()
    {
        var category = await CreateCategoryAsync("NS");
        var versionId = await SaveDraftAsync(category.CategoryId, DescriptionEquals("hit"));
        var corpus = await WriteBoundCorpusAsync([
            ("hit", "no_suggestion", null)
        ]);
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], corpus),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.False(result.Value!.ActivationEligible);
        Assert.True(result.Value.IncorrectApplicationCanaries >= 1);
    }

    [Fact]
    public async Task Coverage_basis_points_are_within_0_to_10000()
    {
        var category = await CreateCategoryAsync("Cov");
        var versionId = await SaveDraftAsync(category.CategoryId, DescriptionEquals("half"));
        var corpus = await WriteBoundCorpusAsync([
            ("half", "suggestion", category.CategoryId),
            ("miss", "no_suggestion", null)
        ]);
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], corpus),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var report = await validationStore.GetReportAsync(connection, null, result.Value!.ValidationId, CancellationToken.None);
        Assert.InRange(report!.CoverageBasisPoints, 0, 10_000);
        Assert.Equal(5000, report.CoverageBasisPoints);
    }

    [Fact]
    public async Task Report_is_immutable_after_completion()
    {
        var category = await CreateCategoryAsync("Imm");
        var versionId = await SaveDraftAsync(category.CategoryId, DescriptionEquals("imm"));
        var corpus = await WriteBoundCorpusAsync([
            ("imm", "suggestion", category.CategoryId)
        ]);
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], corpus),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(async () =>
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE validation_report SET total_rows = 99;";
            await cmd.ExecuteNonQueryAsync();
        });
    }

    [Fact]
    public async Task Candidate_fingerprint_changes_when_candidate_set_changes()
    {
        var category = await CreateCategoryAsync("FpSet");
        var v1 = await SaveDraftAsync(category.CategoryId, DescriptionEquals("fp1"), ruleId: "rule-fp1");
        var v2 = await SaveDraftAsync(category.CategoryId, DescriptionEquals("fp2"), ruleId: "rule-fp2");
        var corpus = await WriteBoundCorpusAsync([
            ("fp1", "suggestion", category.CategoryId)
        ]);
        var a = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [v1], corpus),
            actor,
            NextKey(),
            CancellationToken.None);
        var b = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [v1, v2], corpus),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(a.IsSuccess && b.IsSuccess);
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var runA = await validationStore.GetRunAsync(connection, null, a.Value!.ValidationId, CancellationToken.None);
        var runB = await validationStore.GetRunAsync(connection, null, b.Value!.ValidationId, CancellationToken.None);
        Assert.NotEqual(runA!.CandidateFingerprint, runB!.CandidateFingerprint);
    }

    [Fact]
    public async Task Empty_corpus_file_accounts_zero_rows_but_is_not_activation_eligible()
    {
        var category = await CreateCategoryAsync("EmptyC");
        var versionId = await SaveDraftAsync(category.CategoryId, DescriptionEquals("e"));
        var path = Path.Combine(root, "empty.jsonl");
        WriteOwnerFile(path, "");
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], path),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(0, result.Value!.TotalRows);
        Assert.False(result.Value.ActivationEligible);
    }

    [Fact]
    public async Task Missing_expected_outcome_is_not_activation_eligible()
    {
        var category = await CreateCategoryAsync("MissingExpected");
        var versionId = await SaveDraftAsync(category.CategoryId, DescriptionEquals("match"));
        var corpus = await WriteBoundCorpusAsync([
            ("match", null, null)
        ]);
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], corpus),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.False(result.Value!.ActivationEligible);
    }

    [Fact]
    public async Task Expected_conflict_that_disappears_is_a_drift_canary()
    {
        var category = await CreateCategoryAsync("MissingConflict");
        var versionId = await SaveDraftAsync(category.CategoryId, DescriptionEquals("different"));
        var corpus = await WriteBoundCorpusAsync([
            ("no match", "conflict", null)
        ]);
        var result = await validate.HandleAsync(
            new ClassifyRuleValidateRequest(ClassifyOperationIds.ContractVersion, [versionId], corpus),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.False(result.Value!.ActivationEligible);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        var report = await validationStore.GetReportAsync(connection, null, result.Value.ValidationId, CancellationToken.None);
        Assert.Equal(1, report!.DriftCanaryCount);
    }

    [Fact]
    public void Report_builder_candidate_fingerprint_is_order_insensitive()
    {
        var left = ValidationReportBuilder.ComputeCandidateFingerprint(
        [
            ("rv-b", "cat", new string('b', 64), "normalization_v1", "owner_authored"),
            ("rv-a", "cat", new string('a', 64), "normalization_v1", "owner_authored")
        ]);
        var right = ValidationReportBuilder.ComputeCandidateFingerprint(
        [
            ("rv-a", "cat", new string('a', 64), "normalization_v1", "owner_authored"),
            ("rv-b", "cat", new string('b', 64), "normalization_v1", "owner_authored")
        ]);
        Assert.Equal(left, right);
        Assert.Equal(64, left.Length);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<string> SaveDraftAsync(
        string categoryId,
        ClassificationRuleConditionInput condition,
        string? ruleId = null)
    {
        var result = await save.HandleAsync(
            new ClassifyRuleSaveRequest(
                ClassifyOperationIds.ContractVersion,
                ruleId ?? "rule-" + Guid.NewGuid().ToString("N")[..12],
                null,
                categoryId,
                NormalizationDescriptor.V1.Version,
                [condition],
                "draft for validate"),
            actor,
            NextKey(),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorCode);
        return result.Value!.RuleVersionId;
    }

    private static ClassificationRuleConditionInput DescriptionEquals(string value)
    {
        Assert.True(ClassificationRuleVocabulary.TryCreateCondition(
            0,
            ClassificationRuleVocabulary.DescriptionNormalized,
            ClassificationRuleVocabulary.EqualsPredicate,
            value,
            null, null, null,
            out var condition,
            out _));
        // Wire input uses enums — rebuild as contract input from canonical text.
        return new ClassificationRuleConditionInput(
            0,
            ClassificationRuleFieldKey.DescriptionNormalized,
            ClassificationRulePredicateKind.Equals,
            ValueText: value);
    }

    /// <summary>
    /// Immutable owner-gate corpus: each row is bound to a real LEDGER projection member
    /// (transaction id + lifecycle fingerprint + public amount). Preserves STALE fail-closed
    /// when binding would be wrong.
    /// </summary>
    private async Task<string> WriteBoundCorpusAsync(
        IReadOnlyList<(string Description, string? ExpectedKind, string? ExpectedCategory)> rows)
    {
        var created = new List<(string TxId, string Description)>();
        foreach (var row in rows)
        {
            var tx = await RecordTransactionAsync(row.Description);
            created.Add((tx.TransactionId, row.Description));
        }

        var page = await ledger.QueryClassificationProjectionAsync(
            ClassificationProjectionPurpose.Evaluation,
            ActualsContractVersions.Current,
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
            var sb = new StringBuilder();
            sb.Append("{\"ordinal\":").Append(i.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"transactionId\":").Append(JsonSerializer.Serialize(txId));
            sb.Append(",\"accountId\":").Append(JsonSerializer.Serialize(item.AccountId));
            sb.Append(",\"sourceDescription\":").Append(JsonSerializer.Serialize(description));
            sb.Append(",\"amountDirection\":").Append(JsonSerializer.Serialize(direction));
            sb.Append(",\"amountAbsoluteMinor\":").Append(abs.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"itemLifecycleFingerprint\":").Append(JsonSerializer.Serialize(life));
            if (expected.ExpectedKind is not null)
            {
                sb.Append(",\"expectedOutcomeKind\":").Append(JsonSerializer.Serialize(expected.ExpectedKind));
            }

            if (expected.ExpectedCategory is not null)
            {
                sb.Append(",\"expectedCategoryId\":").Append(JsonSerializer.Serialize(expected.ExpectedCategory));
            }

            sb.Append('}');
            lines.Add(sb.ToString());
        }

        var path = Path.Combine(root, "corpus-" + Guid.NewGuid().ToString("N") + ".jsonl");
        WriteOwnerFile(path, string.Join('\n', lines) + "\n");
        return path;
    }

    private static void WriteOwnerFile(string path, string content)
    {
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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
        // MaskedIdentifier requires 1–4 digits (not all-digit); hex-only suffixes fail closed.
        var digits = (Math.Abs(unique.GetHashCode()) % 9000 + 1000).ToString(CultureInfo.InvariantCulture);
        return await ExecuteSuccessAsync(
            "ledger.account.create",
            new CreateAccountInput("Val Bank " + unique, "Primary-" + unique, AccountType.Cheque, "****" + digits, "ZAR"),
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

    private async Task<TransactionDetail> RecordTransactionAsync(string description)
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
                new RegisterEvidenceInput(
                    EvidenceKind.AgentCapture,
                    digest,
                    "val-capture:" + Guid.NewGuid().ToString("N")[..8],
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
        return JsonSerializer.Deserialize(envelope.Result!.Value, resultType)
            ?? throw new InvalidOperationException("No typed result");
    }

    private string NextKey() => $"val-key-{Interlocked.Increment(ref keySeq)}-{Guid.NewGuid():N}";
}
