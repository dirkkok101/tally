using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Relationships;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Infrastructure.Storage;
using Xunit;

namespace Tally.Tests.Classify.LedgerContract;

/// <summary>
/// TASK-CLASSIFY-RULEBOOK-GATE-INT-LEDGER-CONTRACT / bd-2q0i
/// Proves the complete released LEDGER classification seam CLASSIFY may consume:
/// descriptors, multi-page frozen evaluation, apply-preflight identity coverage,
/// stale mutation preconditions, version/cursor compatibility, and private-boundary isolation.
/// Public surface only — OperationRegistry + released operations; no private Ledger store.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LedgerClassifyPrerequisiteTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-classify-prereq-" + Guid.NewGuid().ToString("N"));
    private TallyProcess process = null!;
    private int keySeq;

    public async Task InitializeAsync()
    {
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        process = new TallyProcess(OperationRegistry.Create(), LedgerServices.Create(database));
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        return Task.CompletedTask;
    }

    // ── 1. Descriptors / schema publication ───────────────────────────────────

    [Fact]
    public void Registry_exposes_actuals_query_classification_projection_surface()
    {
        var op = OperationRegistry.Create().Find("ledger.actuals.query");
        Assert.NotNull(op);
        Assert.Equal("1.0", op!.MinimumContractVersion);
        Assert.Equal("1.0", op.MaximumContractVersion);
        Assert.Equal(typeof(QueryActualsInput), op.RequestTypeInfo.Type);
        Assert.Equal(typeof(ActualsQueryResult), op.ResultTypeInfo.Type);
        Assert.Equal("query", op.Kind);
        Assert.False(op.RequiresIdempotencyKey);

        var request = PropertyNames(op.RequestTypeInfo);
        Assert.Contains("purpose", request, StringComparer.Ordinal);
        Assert.Contains("itemProjection", request, StringComparer.Ordinal);
        Assert.Contains("transactionIds", request, StringComparer.Ordinal);

        var result = PropertyNames(op.ResultTypeInfo);
        foreach (var required in new[]
                 {
                     "snapshotId", "expiresAt", "totalCount", "cursor",
                     "ledgerContractVersion", "storeGenerationFingerprint",
                     "projectionVersion", "categoryIdentityLifecycleFingerprint",
                     "activeCategories", "classificationItems", "missingTransactionIds"
                 })
        {
            Assert.Contains(required, result, StringComparer.Ordinal);
        }

        var schema = op.ToSchema();
        Assert.Contains(ClassificationProjectionVersions.ClassificationV1, schema.Example, StringComparison.Ordinal);
        Assert.Contains("evaluation", schema.RequestSchema, StringComparison.Ordinal);
        Assert.Contains("apply_preflight", schema.RequestSchema, StringComparison.Ordinal);
        Assert.Contains(ActualsErrors.ContractMismatch, schema.Errors.Select(error => error.Code));
    }

    [Fact]
    public void Registry_exposes_revision_aware_category_assign_schema()
    {
        var op = OperationRegistry.Create().Find("ledger.transaction.category.assign");
        Assert.NotNull(op);
        Assert.Equal(typeof(AssignCategoryInput), op!.RequestTypeInfo.Type);
        Assert.Equal(typeof(CategoryAllocationResult), op.ResultTypeInfo.Type);
        Assert.Equal("mutation", op.Kind);
        Assert.True(op.RequiresIdempotencyKey);

        var request = PropertyNames(op.RequestTypeInfo);
        foreach (var required in new[]
                 {
                     "transactionId", "categoryId", "reason",
                     "expectedTransactionRevision", "expectedRelationshipRevision",
                     "expectedAllocationRevision", "expectedActiveAllocationId",
                     "mutationContractVersion"
                 })
        {
            Assert.Contains(required, request, StringComparer.Ordinal);
        }

        var schema = op.ToSchema();
        Assert.Contains(CategoryAllocationMutationVersions.ClassificationV1, schema.Example, StringComparison.Ordinal);
        Assert.Contains(CategoryMutationPreconditionCodes.StalePrecondition, schema.Errors.Select(error => error.Code));
        Assert.Contains(CategoryMutationPreconditionCodes.ContractMismatch, schema.Errors.Select(error => error.Code));
    }

    [Fact]
    public void Registry_exposes_revision_aware_category_correct_schema()
    {
        var op = OperationRegistry.Create().Find("ledger.transaction.category.correct");
        Assert.NotNull(op);
        Assert.Equal(typeof(CorrectCategoryInput), op!.RequestTypeInfo.Type);
        Assert.Equal(typeof(CategoryAllocationResult), op.ResultTypeInfo.Type);
        Assert.Equal("mutation", op.Kind);
        Assert.True(op.RequiresIdempotencyKey);

        var request = PropertyNames(op.RequestTypeInfo);
        foreach (var required in new[]
                 {
                     "transactionId", "categoryId", "reason",
                     "expectedActiveAllocationId", "expectedTransactionRevision",
                     "expectedRelationshipRevision", "expectedAllocationRevision",
                     "mutationContractVersion"
                 })
        {
            Assert.Contains(required, request, StringComparer.Ordinal);
        }

        var schema = op.ToSchema();
        Assert.Contains(CategoryAllocationMutationVersions.ClassificationV1, schema.Example, StringComparison.Ordinal);
        Assert.Contains(CategoryMutationPreconditionCodes.StalePrecondition, schema.Errors.Select(error => error.Code));
        Assert.Contains(CategoryMutationPreconditionCodes.ContractMismatch, schema.Errors.Select(error => error.Code));
    }

    // ── 2. Evaluation projection behavior ────────────────────────────────────

    [Fact]
    public async Task Evaluation_returns_only_uncategorized_active_independent_decisions()
    {
        var account = await CreateAccount();
        var cat = await CreateCategory("Food");
        var uncat = await Record(account.AccountId, 'a');
        var categorized = await Record(account.AccountId, 'b');
        await AssignLegacy(categorized.TransactionId, cat.CategoryId, "owner");

        var page = Success(await Evaluation());
        Assert.Equal(ClassificationProjectionVersions.ClassificationV1, page.ProjectionVersion);
        Assert.Contains(page.ClassificationItems!, item => item.TransactionId == uncat.TransactionId);
        Assert.DoesNotContain(page.ClassificationItems!, item => item.TransactionId == categorized.TransactionId);
        Assert.All(page.ClassificationItems!, item =>
        {
            Assert.Equal(CategoryMutationState.Assignable, item.CategoryMutationState);
            Assert.Equal("none", item.AllocationRevision);
        });
    }

    [Fact]
    public async Task Evaluation_excludes_voided_transfer_principal_and_refund_credit()
    {
        var a = await CreateAccount("Bank A");
        var b = await CreateAccount("Bank B");
        var plain = await Record(a.AccountId, 'c');
        var voided = await Record(a.AccountId, 'd');
        await Void(voided.TransactionId);
        var outflow = await Record(a.AccountId, 'e', "-10.00");
        var inflow = await Record(b.AccountId, 'f', "10.00");
        await ConfirmTransfer(outflow.TransactionId, inflow.TransactionId);
        var original = await Record(a.AccountId, 'g', "-25.00");
        var credit = await Record(a.AccountId, 'h', "25.00");
        await ConfirmRefund(original.TransactionId, credit.TransactionId);

        var page = Success(await Evaluation());
        Assert.Contains(page.ClassificationItems!, item => item.TransactionId == plain.TransactionId);
        Assert.DoesNotContain(page.ClassificationItems!, item => item.TransactionId == voided.TransactionId);
        Assert.DoesNotContain(page.ClassificationItems!, item => item.TransactionId == outflow.TransactionId);
        Assert.DoesNotContain(page.ClassificationItems!, item => item.TransactionId == inflow.TransactionId);
        Assert.DoesNotContain(page.ClassificationItems!, item => item.TransactionId == credit.TransactionId);
    }

    [Fact]
    public async Task Multi_page_evaluation_preserves_frozen_membership_and_dense_ordinals()
    {
        var account = await CreateAccount();
        for (var i = 0; i < 5; i++)
        {
            await Record(account.AccountId, (char)('A' + i));
        }

        var first = Success(await Evaluation(pageSize: 2));
        Assert.Equal(2, first.ClassificationItems!.Count);
        Assert.Equal(5, first.TotalCount);
        Assert.NotNull(first.Cursor);
        Assert.False(string.IsNullOrWhiteSpace(first.SnapshotId));
        Assert.False(string.IsNullOrWhiteSpace(first.CategoryIdentityLifecycleFingerprint));

        var second = Success(await EvaluationContinue(first.Cursor!));
        Assert.Equal(2, second.ClassificationItems!.Count);
        Assert.Equal(first.SnapshotId, second.SnapshotId);
        Assert.Equal(first.TotalCount, second.TotalCount);
        Assert.Equal(first.CategoryIdentityLifecycleFingerprint, second.CategoryIdentityLifecycleFingerprint);
        Assert.Equal(
            first.ActiveCategories!.Select(c => c.CategoryId).ToArray(),
            second.ActiveCategories!.Select(c => c.CategoryId).ToArray());

        var third = Success(await EvaluationContinue(second.Cursor!));
        Assert.Single(third.ClassificationItems!);
        Assert.Null(third.Cursor);
        Assert.Equal(first.SnapshotId, third.SnapshotId);

        // Direct concatenated order — no re-sort of ordinals.
        var concatenated = first.ClassificationItems!
            .Concat(second.ClassificationItems!)
            .Concat(third.ClassificationItems!)
            .ToArray();
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, concatenated.Select(item => item.Ordinal).ToArray());
        Assert.Equal(5, concatenated.Select(item => item.TransactionId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Evaluation_first_page_exposes_descriptor_and_catalogue_fields()
    {
        var account = await CreateAccount();
        var active = await CreateCategory("ActiveGate");
        var archived = await CreateCategory("ArchivedGate");
        await ArchiveCategory(archived.CategoryId);
        await Record(account.AccountId, 'k');

        var page = Success(await Evaluation());
        Assert.Equal(ClassificationProjectionVersions.ClassificationV1, page.ProjectionVersion);
        Assert.Equal("1.0", page.LedgerContractVersion);
        Assert.False(string.IsNullOrWhiteSpace(page.StoreGenerationFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(page.ExpiresAt));
        Assert.Contains(page.ActiveCategories!, c => c.CategoryId == active.CategoryId && c.LifecycleState == "active");
        Assert.DoesNotContain(page.ActiveCategories!, c => c.CategoryId == archived.CategoryId);
    }

    // ── 3. Compatibility / cursor failures → no partial evaluation ───────────

    [Fact]
    public async Task Incompatible_item_projection_fails_without_partial_evaluation()
    {
        var result = await Query(new QueryActualsInput(
            Purpose: ClassificationProjectionPurpose.Evaluation,
            ItemProjection: "classification_v0"));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(ActualsErrors.ContractMismatch, ErrorCode(result));
        AssertNoPartialResult(result);
    }

    [Fact]
    public async Task Missing_item_projection_fails_without_partial_evaluation()
    {
        var result = await Query(new QueryActualsInput(
            Purpose: ClassificationProjectionPurpose.Evaluation,
            ItemProjection: null));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(ActualsErrors.ContractMismatch, ErrorCode(result));
        AssertNoPartialResult(result);
    }

    [Fact]
    public async Task Invalid_classification_cursor_fails_without_partial_evaluation()
    {
        var result = await Query(new QueryActualsInput(
            Purpose: ClassificationProjectionPurpose.Evaluation,
            ItemProjection: ClassificationProjectionVersions.ClassificationV1,
            Cursor: "not-a-valid-cursor"));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(ActualsErrors.CursorInvalid, ErrorCode(result));
        AssertNoPartialResult(result);
    }

    [Fact]
    public async Task Expired_classification_cursor_fails_without_partial_evaluation()
    {
        var account = await CreateAccount();
        for (var i = 0; i < 3; i++)
        {
            await Record(account.AccountId, (char)('m' + i));
        }

        var first = Success(await Evaluation(pageSize: 1));
        Assert.NotNull(first.Cursor);

        var tampered = TamperCursorExpiry(first.Cursor!, DateTimeOffset.UtcNow.AddMinutes(-1));
        var result = await Query(new QueryActualsInput(
            Purpose: ClassificationProjectionPurpose.Evaluation,
            ItemProjection: ClassificationProjectionVersions.ClassificationV1,
            Cursor: tampered));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(ActualsErrors.SnapshotExpired, ErrorCode(result));
        AssertNoPartialResult(result);
    }

    [Fact]
    public async Task Contract_version_mismatch_on_classification_cursor_fails_without_partial_evaluation()
    {
        var account = await CreateAccount();
        for (var i = 0; i < 3; i++)
        {
            await Record(account.AccountId, (char)('p' + i));
        }

        var first = Success(await Evaluation(pageSize: 1));
        Assert.NotNull(first.Cursor);

        var tampered = TamperCursorContract(first.Cursor!, "9.9");
        var result = await Query(new QueryActualsInput(
            Purpose: ClassificationProjectionPurpose.Evaluation,
            ItemProjection: ClassificationProjectionVersions.ClassificationV1,
            Cursor: tampered));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(ActualsErrors.ContractMismatch, ErrorCode(result));
        AssertNoPartialResult(result);
    }

    // ── 4. Apply preflight ───────────────────────────────────────────────────

    [Fact]
    public async Task Apply_preflight_represents_every_selected_identity_and_missing_ids()
    {
        var account = await CreateAccount();
        var cat = await CreateCategory("Bills");
        var uncat = await Record(account.AccountId, 's');
        var catTx = await Record(account.AccountId, 't');
        await AssignLegacy(catTx.TransactionId, cat.CategoryId, "owner");
        var missingId = LedgerId.New().ToString();

        var page = Success(await Preflight([uncat.TransactionId, catTx.TransactionId, missingId]));
        Assert.Equal(2, page.ClassificationItems!.Count);
        Assert.Equal([missingId], page.MissingTransactionIds);

        var assignable = page.ClassificationItems.Single(item => item.TransactionId == uncat.TransactionId);
        var correctable = page.ClassificationItems.Single(item => item.TransactionId == catTx.TransactionId);
        Assert.Equal(CategoryMutationState.Assignable, assignable.CategoryMutationState);
        Assert.Equal(CategoryMutationState.Correctable, correctable.CategoryMutationState);
        Assert.Equal(cat.CategoryId, correctable.CurrentCategoryId);
        Assert.NotNull(correctable.CurrentAllocationId);
        Assert.NotEqual("none", correctable.AllocationRevision);
        Assert.False(string.IsNullOrWhiteSpace(assignable.TransactionRevision));
        Assert.False(string.IsNullOrWhiteSpace(assignable.RelationshipRevision));
        Assert.False(string.IsNullOrWhiteSpace(assignable.AllocationRevision));
    }

    [Fact]
    public async Task Apply_preflight_marks_voided_transactions_ineligible()
    {
        var account = await CreateAccount();
        var voided = await Record(account.AccountId, 'u');
        await Void(voided.TransactionId);

        var page = Success(await Preflight([voided.TransactionId]));
        var item = Assert.Single(page.ClassificationItems!);
        Assert.Equal(CategoryMutationState.Ineligible, item.CategoryMutationState);
    }

    [Fact]
    public async Task Apply_preflight_rejects_unbounded_or_empty_id_sets()
    {
        var empty = await Query(new QueryActualsInput(
            Purpose: ClassificationProjectionPurpose.ApplyPreflight,
            ItemProjection: ClassificationProjectionVersions.ClassificationV1,
            TransactionIds: []));
        Assert.NotEqual(0, empty.ExitCode);
        Assert.Equal(ActualsErrors.InvalidFilter, ErrorCode(empty));
        AssertNoPartialResult(empty);

        var tooMany = Enumerable.Range(0, ClassificationProjectionVersions.MaxApplyPreflightIds + 1)
            .Select(_ => LedgerId.New().ToString())
            .ToArray();
        var oversized = await Query(new QueryActualsInput(
            Purpose: ClassificationProjectionPurpose.ApplyPreflight,
            ItemProjection: ClassificationProjectionVersions.ClassificationV1,
            TransactionIds: tooMany));
        Assert.NotEqual(0, oversized.ExitCode);
        Assert.Equal(ActualsErrors.InvalidFilter, ErrorCode(oversized));
        AssertNoPartialResult(oversized);
    }

    // ── 5. Mutation preconditions (stale before mutation) ─────────────────────

    [Fact]
    public async Task Preflight_intervening_assign_rejects_stale_classification_assign_before_mutation()
    {
        var account = await CreateAccount();
        var first = await CreateCategory("RaceFirst");
        var second = await CreateCategory("RaceSecond");
        var tx = await Record(account.AccountId, 'v');

        var preflight = Success(await Preflight([tx.TransactionId]));
        var item = Assert.Single(preflight.ClassificationItems!);
        Assert.Equal("none", item.AllocationRevision);

        // Intervening legacy assign after preflight.
        await AssignLegacy(tx.TransactionId, first.CategoryId, "intervening");

        var result = await Assign(new AssignCategoryInput(
            tx.TransactionId,
            second.CategoryId,
            "stale race",
            ExpectedTransactionRevision: item.TransactionRevision,
            ExpectedRelationshipRevision: item.RelationshipRevision,
            ExpectedAllocationRevision: item.AllocationRevision,
            MutationContractVersion: CategoryAllocationMutationVersions.ClassificationV1), "race-stale");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(CategoryMutationPreconditionCodes.StalePrecondition, ErrorCode(result));
        AssertNoPartialResult(result);

        var after = await GetTransaction(tx.TransactionId);
        Assert.Equal(first.CategoryId, after.Category.CategoryId);
    }

    [Fact]
    public async Task Matching_preflight_preconditions_allow_classification_assign()
    {
        var account = await CreateAccount();
        var cat = await CreateCategory("OkAssign");
        var tx = await Record(account.AccountId, 'w');
        var preflight = Success(await Preflight([tx.TransactionId]));
        var item = Assert.Single(preflight.ClassificationItems!);

        var allocated = Allocation(await Assign(new AssignCategoryInput(
            tx.TransactionId,
            cat.CategoryId,
            "owner",
            ExpectedTransactionRevision: item.TransactionRevision,
            ExpectedRelationshipRevision: item.RelationshipRevision,
            ExpectedAllocationRevision: item.AllocationRevision,
            MutationContractVersion: CategoryAllocationMutationVersions.ClassificationV1), "ok-assign"));
        Assert.Equal(cat.CategoryId, allocated.Transaction.Category.CategoryId);
    }

    [Fact]
    public async Task Stale_allocation_identity_on_correct_is_rejected_before_mutation()
    {
        var account = await CreateAccount();
        var first = await CreateCategory("Orig");
        var second = await CreateCategory("Next");
        var tx = await Record(account.AccountId, 'x');
        await AssignLegacy(tx.TransactionId, first.CategoryId, "initial");
        var preflight = Success(await Preflight([tx.TransactionId]));
        var item = Assert.Single(preflight.ClassificationItems!);
        var before = await GetTransaction(tx.TransactionId);

        var result = await Correct(new CorrectCategoryInput(
            tx.TransactionId,
            second.CategoryId,
            "stale correction",
            ExpectedActiveAllocationId: LedgerId.New().ToString(),
            ExpectedTransactionRevision: item.TransactionRevision,
            ExpectedRelationshipRevision: item.RelationshipRevision,
            ExpectedAllocationRevision: item.AllocationRevision,
            MutationContractVersion: CategoryAllocationMutationVersions.ClassificationV1), "correct-stale-id");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(CategoryMutationPreconditionCodes.StalePrecondition, ErrorCode(result));
        AssertNoPartialResult(result);

        var after = await GetTransaction(tx.TransactionId);
        Assert.Equal(before.Category.AllocationEventId, after.Category.AllocationEventId);
        Assert.Equal(first.CategoryId, after.Category.CategoryId);
    }

    [Fact]
    public async Task Stale_allocation_revision_on_assign_is_rejected_before_mutation()
    {
        var account = await CreateAccount();
        var cat = await CreateCategory("DriftAllocation");
        var tx = await Record(account.AccountId, 'y');
        var preflight = Success(await Preflight([tx.TransactionId]));
        var item = Assert.Single(preflight.ClassificationItems!);

        var result = await Assign(new AssignCategoryInput(
            tx.TransactionId,
            cat.CategoryId,
            "owner",
            ExpectedTransactionRevision: item.TransactionRevision,
            ExpectedRelationshipRevision: item.RelationshipRevision,
            ExpectedAllocationRevision: "allocation:drift:token",
            MutationContractVersion: CategoryAllocationMutationVersions.ClassificationV1), "drift-allocation");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(CategoryMutationPreconditionCodes.StalePrecondition, ErrorCode(result));
        AssertNoPartialResult(result);

        var after = await GetTransaction(tx.TransactionId);
        Assert.Null(after.Category.CategoryId);
    }

    [Fact]
    public async Task Stale_transaction_revision_on_assign_is_rejected_before_mutation()
    {
        var account = await CreateAccount();
        var cat = await CreateCategory("DriftTxn");
        var tx = await Record(account.AccountId, 'y');
        var preflight = Success(await Preflight([tx.TransactionId]));
        var item = Assert.Single(preflight.ClassificationItems!);

        var result = await Assign(new AssignCategoryInput(
            tx.TransactionId,
            cat.CategoryId,
            "owner",
            ExpectedTransactionRevision: "genesis:not-this-id",
            ExpectedRelationshipRevision: item.RelationshipRevision,
            ExpectedAllocationRevision: item.AllocationRevision,
            MutationContractVersion: CategoryAllocationMutationVersions.ClassificationV1), "drift-txn");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(CategoryMutationPreconditionCodes.StalePrecondition, ErrorCode(result));
        AssertNoPartialResult(result);

        var after = await GetTransaction(tx.TransactionId);
        Assert.Null(after.Category.CategoryId);
    }

    [Fact]
    public async Task Stale_relationship_revision_on_assign_is_rejected_before_mutation()
    {
        var account = await CreateAccount();
        var cat = await CreateCategory("DriftRel");
        var tx = await Record(account.AccountId, 'z');
        var preflight = Success(await Preflight([tx.TransactionId]));
        var item = Assert.Single(preflight.ClassificationItems!);

        var result = await Assign(new AssignCategoryInput(
            tx.TransactionId,
            cat.CategoryId,
            "owner",
            ExpectedTransactionRevision: item.TransactionRevision,
            ExpectedRelationshipRevision: "relationship:drift:token",
            ExpectedAllocationRevision: item.AllocationRevision,
            MutationContractVersion: CategoryAllocationMutationVersions.ClassificationV1), "drift-rel");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(CategoryMutationPreconditionCodes.StalePrecondition, ErrorCode(result));
        AssertNoPartialResult(result);

        var after = await GetTransaction(tx.TransactionId);
        Assert.Null(after.Category.CategoryId);
    }

    [Fact]
    public async Task Incompatible_mutation_contract_version_fails_before_mutation()
    {
        var account = await CreateAccount();
        var cat = await CreateCategory("MutVersion");
        var tx = await Record(account.AccountId, '1');

        var result = await Assign(new AssignCategoryInput(
            tx.TransactionId,
            cat.CategoryId,
            "owner",
            MutationContractVersion: "classification_v0"), "mut-version");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(CategoryMutationPreconditionCodes.ContractMismatch, ErrorCode(result));
        AssertNoPartialResult(result);

        var after = await GetTransaction(tx.TransactionId);
        Assert.Null(after.Category.CategoryId);
    }

    [Fact]
    public async Task Matching_preflight_preconditions_allow_classification_correct()
    {
        var account = await CreateAccount();
        var first = await CreateCategory("From");
        var second = await CreateCategory("To");
        var tx = await Record(account.AccountId, '2');
        await AssignLegacy(tx.TransactionId, first.CategoryId, "initial");
        var preflight = Success(await Preflight([tx.TransactionId]));
        var item = Assert.Single(preflight.ClassificationItems!);

        var corrected = Allocation(await Correct(new CorrectCategoryInput(
            tx.TransactionId,
            second.CategoryId,
            "owner corrected",
            ExpectedActiveAllocationId: item.CurrentAllocationId,
            ExpectedTransactionRevision: item.TransactionRevision,
            ExpectedRelationshipRevision: item.RelationshipRevision,
            ExpectedAllocationRevision: item.AllocationRevision,
            MutationContractVersion: CategoryAllocationMutationVersions.ClassificationV1), "correct-ok"));
        Assert.Equal(second.CategoryId, corrected.Transaction.Category.CategoryId);
    }

    // ── 6. Architecture: CLASSIFY must not touch private Ledger ──────────────

    [Fact]
    public void Classify_production_code_does_not_reference_private_ledger_surface()
    {
        // DD-CLASSIFY-LEDGER-PUBLIC-PROJECTION: CLASSIFY consumes only public LEDGER operations.
        var repoRoot = FindRepositoryRoot();
        var classifyRoots = new[]
        {
            Path.Combine(repoRoot, "src", "Tally", "Features", "Classify"),
            Path.Combine(repoRoot, "src", "Tally", "Domain", "Classify"),
            Path.Combine(repoRoot, "src", "Tally", "Infrastructure", "Classify"),
            Path.Combine(repoRoot, "src", "Tally", "Integration", "Classify"),
            Path.Combine(repoRoot, "src", "Tally", "Contracts", "Classify")
        };

        string[] forbiddenLedgerPrivate =
        [
            "LedgerDb",
            "LedgerConnectionFactory",
            "LedgerSchema",
            "QuerySnapshotStore",
            "ActualsQueryHandler",
            "CategoryAllocationHandlers",
            "CategoryAllocationStore",
            "CategoryStore",
            "TransactionStore",
            "RelationshipStore",
            "Tally.Domain.Ledger",
            "Tally.Features.Ledger",
            "Tally.Infrastructure.Storage",
            "ledger.db",
            "Microsoft.Data.Sqlite",
            "SqliteConnection"
        ];

        var scanned = 0;
        foreach (var classifyRoot in classifyRoots)
        {
            if (!Directory.Exists(classifyRoot)) continue;
            foreach (var file in Directory.EnumerateFiles(classifyRoot, "*.cs", SearchOption.AllDirectories))
            {
                scanned++;
                var source = File.ReadAllText(file);
                foreach (var token in forbiddenLedgerPrivate)
                {
                    Assert.False(
                        source.Contains(token, StringComparison.Ordinal),
                        $"CLASSIFY production file {file} must not reference Ledger private surface token '{token}'.");
                }
            }
        }

        // Empty production tree is valid at this gate (client beads not started); zero private refs holds.
        Assert.True(scanned >= 0);
    }

    [Fact]
    public void Classify_production_tree_does_not_embed_private_ledger_storage_paths()
    {
        var repoRoot = FindRepositoryRoot();
        var classifyRoots = new[]
        {
            Path.Combine(repoRoot, "src", "Tally", "Features", "Classify"),
            Path.Combine(repoRoot, "src", "Tally", "Domain", "Classify"),
            Path.Combine(repoRoot, "src", "Tally", "Infrastructure", "Classify"),
            Path.Combine(repoRoot, "src", "Tally", "Integration", "Classify")
        };

        string[] pathTokens =
        [
            "ledger.db",
            "LedgerDb",
            "LedgerConnectionFactory",
            "LedgerRuntimeBootstrap",
            "tally-ledger",
            "/ledger/",
            "query_snapshot",
            "current_category_allocation"
        ];

        foreach (var rootPath in classifyRoots)
        {
            if (!Directory.Exists(rootPath)) continue;
            foreach (var file in Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories))
            {
                var source = File.ReadAllText(file);
                foreach (var token in pathTokens)
                {
                    Assert.False(
                        source.Contains(token, StringComparison.OrdinalIgnoreCase),
                        $"CLASSIFY production file {file} must not reference Ledger storage path/config token '{token}'.");
                }
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Task<ProcessResult> Evaluation(int? pageSize = null) =>
        Query(new QueryActualsInput(
            Purpose: ClassificationProjectionPurpose.Evaluation,
            ItemProjection: ClassificationProjectionVersions.ClassificationV1,
            PageSize: pageSize));

    private Task<ProcessResult> EvaluationContinue(string cursor) =>
        Query(new QueryActualsInput(
            Purpose: ClassificationProjectionPurpose.Evaluation,
            ItemProjection: ClassificationProjectionVersions.ClassificationV1,
            Cursor: cursor));

    private Task<ProcessResult> Preflight(IReadOnlyList<string> ids) =>
        Query(new QueryActualsInput(
            Purpose: ClassificationProjectionPurpose.ApplyPreflight,
            ItemProjection: ClassificationProjectionVersions.ClassificationV1,
            TransactionIds: ids));

    private Task<ProcessResult> Query(QueryActualsInput input) =>
        Run("ledger.actuals.query", JsonSerializer.SerializeToElement(input, ActualsJsonContext.Default.QueryActualsInput), key: null);

    private async Task<AccountDetail> CreateAccount(string bank = "Prereq Bank")
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var input = new CreateAccountInput(bank + " " + unique, "Primary-" + unique, AccountType.Cheque, "****" + unique[..4], "ZAR");
        return Success(await Run("ledger.account.create", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.CreateAccountInput), NextKey()), LedgerJsonContext.Default.AccountDetail);
    }

    private async Task<CategoryDetail> CreateCategory(string name)
    {
        var input = new CreateCategoryInput(name + "-" + Guid.NewGuid().ToString("N")[..6]);
        return Success(await Run("ledger.category.create", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.CreateCategoryInput), NextKey()), LedgerJsonContext.Default.CategoryDetail);
    }

    private Task<ProcessResult> ArchiveCategory(string categoryId) =>
        Run("ledger.category.archive", JsonSerializer.SerializeToElement(new ArchiveCategoryInput(categoryId, "archive"), LedgerJsonContext.Default.ArchiveCategoryInput), NextKey());

    private async Task<TransactionDetail> Record(string accountId, char digest, string amount = "-12.34")
    {
        var digestText = string.Concat(Enumerable.Repeat(((byte)digest).ToString("x2", System.Globalization.CultureInfo.InvariantCulture), 32));
        var input = new RecordTransactionInput(
            accountId, amount, "ZAR", "2026-07-15", null, "Prereq purchase " + digest + Guid.NewGuid().ToString("N")[..4], null, null,
            new RegisterEvidenceInput(EvidenceKind.AgentCapture, digestText, "prereq-capture:" + digest + ":" + Guid.NewGuid().ToString("N")[..8], null, null));
        return Success(await Run("ledger.transaction.record", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.RecordTransactionInput), NextKey()), LedgerJsonContext.Default.TransactionDetail);
    }

    private Task<ProcessResult> AssignLegacy(string transactionId, string categoryId, string reason) =>
        Assign(new AssignCategoryInput(transactionId, categoryId, reason), NextKey());

    private Task<ProcessResult> Assign(AssignCategoryInput input, string key) =>
        Run("ledger.transaction.category.assign", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.AssignCategoryInput), key);

    private Task<ProcessResult> Correct(CorrectCategoryInput input, string key) =>
        Run("ledger.transaction.category.correct", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.CorrectCategoryInput), key);

    private Task<ProcessResult> Void(string transactionId) =>
        Run("ledger.transaction.void",
            JsonSerializer.SerializeToElement(new VoidTransactionInput(transactionId, "void for prereq"), TransactionCorrectionJsonContext.Default.VoidTransactionInput),
            NextKey());

    private Task<ProcessResult> ConfirmTransfer(string outflowId, string inflowId) =>
        Run("ledger.transfer.confirm",
            JsonSerializer.SerializeToElement(new ConfirmTransferInput(outflowId, inflowId, "owner transfer"), LedgerJsonContext.Default.ConfirmTransferInput),
            NextKey());

    private Task<ProcessResult> ConfirmRefund(string originalId, string creditId) =>
        Run("ledger.refund.confirm",
            JsonSerializer.SerializeToElement(new ConfirmRefundInput(originalId, creditId, "owner refund"), LedgerJsonContext.Default.ConfirmRefundInput),
            NextKey());

    private async Task<TransactionDetail> GetTransaction(string transactionId)
    {
        var input = new GetTransactionInput(transactionId, true);
        return Success(await Run("ledger.transaction.get", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.GetTransactionInput), key: null), LedgerJsonContext.Default.TransactionDetail);
    }

    private async Task<ProcessResult> Run(string operationId, JsonElement input, string? key)
    {
        var descriptor = OperationRegistry.Create().Find(operationId)!;
        var actor = new SafeActor("human", "classify-prereq", "run-01");
        var body = JsonSerializer.Serialize(
            new RequestEnvelope("1.0", actor, input, key),
            LedgerJsonContext.Default.RequestEnvelope);
        var args = descriptor.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1).Concat(["--input", "-"]).ToArray();
        return await process.RunAsync(args, body, CancellationToken.None);
    }

    private static ActualsQueryResult Success(ProcessResult result) =>
        Success(result, ActualsJsonContext.Default.ActualsQueryResult);

    private static CategoryAllocationResult Allocation(ProcessResult result) =>
        Success(result, LedgerJsonContext.Default.CategoryAllocationResult);

    private static T Success<T>(ProcessResult result, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        return JsonSerializer.Deserialize(document.RootElement.GetProperty("result").GetRawText(), typeInfo)!;
    }

    private static string ErrorCode(ProcessResult result)
    {
        using var document = JsonDocument.Parse(result.Stdout);
        return document.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private static void AssertNoPartialResult(ProcessResult result)
    {
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("error", document.RootElement.GetProperty("outcome").GetString());
        Assert.False(document.RootElement.TryGetProperty("result", out var resultElement) && resultElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined);
        Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty("error").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("error").GetProperty("code").GetString()));
    }

    private static IEnumerable<string> PropertyNames(System.Text.Json.Serialization.Metadata.JsonTypeInfo typeInfo) =>
        typeInfo.Properties.Select(property => property.Name);

    private static string TamperCursorExpiry(string cursor, DateTimeOffset expiry)
    {
        var payload = Decode(cursor);
        var next = payload with
        {
            ExpiresAt = expiry.UtcDateTime.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                System.Globalization.CultureInfo.InvariantCulture)
        };
        return Encode(next);
    }

    private static string TamperCursorContract(string cursor, string version)
    {
        var payload = Decode(cursor);
        return Encode(payload with { ContractVersion = version });
    }

    private static ActualsCursorPayload Decode(string value)
    {
        var encoded = value.Replace('-', '+').Replace('_', '/');
        encoded += new string('=', (4 - encoded.Length % 4) % 4);
        return JsonSerializer.Deserialize(Convert.FromBase64String(encoded), ActualsJsonContext.Default.ActualsCursorPayload)!;
    }

    private static string Encode(ActualsCursorPayload payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, ActualsJsonContext.Default.ActualsCursorPayload);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tally.slnx"))) return directory.FullName;
        }

        throw new InvalidOperationException("Could not locate the Tally repository root.");
    }

    private string NextKey() => "classify-prereq-" + Interlocked.Increment(ref keySeq).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
}
