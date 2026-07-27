using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Insights;
using Tally.Contracts.Budget.Plans;
using Tally.Contracts.Budget.Position;
using Tally.Contracts.Budget.Projection;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Budget.Position;
using Tally.Features.Budget.Contract;
using Tally.Features.Budget.Plans.Activate;
using Tally.Features.Budget.Plans.CreateDraft;
using Tally.Features.Budget.Plans.GetRevision;
using Tally.Features.Budget.Position.Get;
using Tally.Features.Budget.Projection;
using Tally.Infrastructure.Budget.Storage;
using Tally.Infrastructure.Budget.Storage.Idempotency;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Budget.InsightsContract;

/// <summary>
/// TC-BUDGET-INSIGHTS-PROJECTION-CONTRACT / FR-BUDGET-INSIGHTS-PROJECTION
/// Read-only INSIGHTS projection: three-op capability, all plan states, single-snapshot
/// binding, limits, compatibility, calculator exclusion, mutation/analytics exclusion.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class BudgetInsightsContractTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-budget-insights-{Guid.NewGuid():N}");
    private readonly SafeActor actor = new("automation", "budget-insights", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerContractClient ledger = null!;
    private BudgetStateStore store = null!;
    private BudgetMutationExecutor executor = null!;
    private CreateBudgetDraftCommand draftCommand = null!;
    private ActivateBudgetPlanRevisionCommand activateCommand = null!;
    private GetBudgetPlanRevisionQuery revisionQuery = null!;
    private GetBudgetPositionQuery positionQuery = null!;
    private GetBudgetInsightEvidenceQuery evidenceQuery = null!;
    private ManualTimeProvider clock = null!;
    private string accountId = null!;
    private int keySeq;

    public async Task InitializeAsync()
    {
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        registry = OperationRegistry.Create();
        process = new TallyProcess(registry, LedgerServices.Create(database));
        ledger = new LedgerContractClient(registry, process);

        var budgetServices = await BudgetStateExtensions.CreateStateAsync(root, CancellationToken.None);
        store = budgetServices.Store;
        executor = new BudgetMutationExecutor(store, budgetServices.Idempotency);

        // Mid-July 2026: July Current; August Future; June Closed.
        clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        draftCommand = new CreateBudgetDraftCommand(executor, ledger, clock);
        activateCommand = new ActivateBudgetPlanRevisionCommand(executor, ledger, clock);
        revisionQuery = new GetBudgetPlanRevisionQuery(store, ledger, clock);
        positionQuery = new GetBudgetPositionQuery(store, ledger, clock);
        evidenceQuery = new GetBudgetInsightEvidenceQuery(store, ledger, revisionQuery, clock);

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

    // ── Exact capability ─────────────────────────────────────────────────────

    // FR-BUDGET-INSIGHTS-PROJECTION / capability set
    [Fact]
    public void Capability_contains_exactly_three_read_operations_in_canonical_order()
    {
        var capability = new BudgetReadProjectionModule().CreateCapability();

        Assert.Equal(BudgetOperationIds.ContractVersion, capability.ContractVersion);
        Assert.Equal(BudgetOperationIds.ContractVersion, capability.MinimumContractVersion);
        Assert.Equal(BudgetOperationIds.ContractVersion, capability.MaximumContractVersion);
        Assert.Equal(
            BudgetReadCapabilityOperations.All,
            capability.AllowedOperations.Select(o => o.OperationId));
        Assert.Equal(3, capability.AllowedOperations.Count);
        Assert.All(capability.AllowedOperations, op =>
        {
            Assert.Equal("query", op.Kind);
            Assert.False(op.RequiresIdempotencyKey);
            Assert.False(string.IsNullOrWhiteSpace(op.RequestSchemaFingerprint));
            Assert.False(string.IsNullOrWhiteSpace(op.ResultSchemaFingerprint));
            Assert.Equal(64, op.RequestSchemaFingerprint.Length);
            Assert.Equal(64, op.ResultSchemaFingerprint.Length);
            Assert.NotEmpty(op.ErrorCodes);
            Assert.Equal("1.0", op.MinimumContractVersion);
            Assert.Equal("1.0", op.MaximumContractVersion);
        });
    }

    // FR-BUDGET-INSIGHTS-PROJECTION / mutation exclusion
    [Fact]
    public void Capability_excludes_all_mutation_and_idempotency_operations()
    {
        var capability = BudgetReadProjectionModule.CreateDescriptorTemplate();
        var ids = capability.AllowedOperations.Select(o => o.OperationId).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(BudgetOperationIds.DraftCreate, ids);
        Assert.DoesNotContain(BudgetOperationIds.RevisionActivate, ids);
        Assert.DoesNotContain(BudgetOperationIds.RevisionList, ids);
        Assert.DoesNotContain("budget.plan.draft.create", ids);
        Assert.DoesNotContain("budget.plan.revision.activate", ids);
        Assert.All(capability.AllowedOperations, op => Assert.False(op.RequiresIdempotencyKey));
        Assert.False(BudgetReadProjectionModule.IsAllowedReadOperation(BudgetOperationIds.DraftCreate));
        Assert.True(BudgetReadProjectionModule.IsAllowedReadOperation(BudgetOperationIds.InsightsEvidenceGet));
    }

    // FR-BUDGET-INSIGHTS-PROJECTION / evidence limits published
    [Fact]
    public void Insights_evidence_capability_publishes_member_limits()
    {
        var evidence = new BudgetReadProjectionModule().CreateCapability()
            .AllowedOperations.Single(o => o.OperationId == BudgetOperationIds.InsightsEvidenceGet);

        Assert.Equal(GetBudgetInsightEvidenceQuery.DefaultMemberLimit, evidence.DefaultLimit);
        Assert.Equal(GetBudgetInsightEvidenceQuery.MaxMemberLimit, evidence.MaxLimit);
        Assert.Equal(100_000, evidence.MaxLimit);
        Assert.Contains(BudgetErrors.ResourceLimit, evidence.ErrorCodes);
        Assert.Contains(BudgetErrors.SourceStateChanged, evidence.ErrorCodes);
    }

    // ── Schema parity with owner reads ───────────────────────────────────────

    // FR-BUDGET-INSIGHTS-PROJECTION / schema fingerprints match owner inventory
    [Fact]
    public void Schema_fingerprints_match_owner_BudgetOperationModule_descriptors()
    {
        var owner = BudgetOperationModule.CreateDescriptorTemplates();
        var capability = new BudgetReadProjectionModule(owner).CreateCapability();

        foreach (var op in capability.AllowedOperations)
        {
            var descriptor = owner.Descriptors.Single(d => d.OperationId == op.OperationId);
            var schema = descriptor.ToSchema();
            Assert.Equal(Sha256Hex(schema.RequestSchema), op.RequestSchemaFingerprint);
            Assert.Equal(Sha256Hex(schema.ResultSchema), op.ResultSchemaFingerprint);
        }
    }

    // ── All plan states ──────────────────────────────────────────────────────

    // FR-BUDGET-INSIGHTS-PROJECTION / BoundRevision
    [Fact]
    public async Task BoundRevision_returns_plan_position_members_and_shared_provenance()
    {
        var cat = await CreateCategoryAsync("BoundCat");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 10_000)], "bound");
        var activated = await ActivateAsync(draft.Value!.Revision.RevisionId, "go");
        var tx = await RecordAsync("-25.00", "2026-07-05", "bound-spend");
        await AssignCategoryAsync(tx.TransactionId, cat.CategoryId);

        var result = await GetEvidenceAsync(Period(2026, 7));
        Assert.True(result.IsSuccess, result.ErrorCode);
        var evidence = result.Value!.Evidence;

        Assert.Equal(BudgetInsightPlanState.BoundRevision, evidence.PlanState);
        Assert.NotNull(evidence.Revision);
        Assert.NotNull(evidence.Position);
        Assert.Equal(activated.Value!.Activated.RevisionId, evidence.Revision!.RevisionId);
        Assert.Equal(activated.Value.Activated.RevisionId, evidence.Position!.RevisionId);
        Assert.Equal(BudgetPositionCalculator.CalculationSchemaVersion, evidence.CalculationSchemaVersion);
        Assert.Equal(evidence.Position.CalculationSchemaVersion, evidence.CalculationSchemaVersion);
        Assert.Equal(2_500, evidence.BudgetActualTotalMinorUnits);
        Assert.Equal(evidence.BudgetActualTotalMinorUnits, evidence.Position.Totals.ActualMinorUnits);
        Assert.NotEmpty(evidence.ActualMembers);
        Assert.Equal(evidence.Ledger.SnapshotId, evidence.Position.Ledger.SnapshotId);
        Assert.Equal(
            evidence.Ledger.StoreGenerationFingerprint,
            evidence.Position.Ledger.StoreGenerationFingerprint);
        Assert.False(string.IsNullOrWhiteSpace(evidence.BindingFingerprint));
        Assert.Equal(64, evidence.BindingFingerprint.Length);
    }

    // FR-BUDGET-INSIGHTS-PROJECTION / NoBudgetPlan
    [Fact]
    public async Task NoBudgetPlan_returns_actuals_without_plan_position_or_calculator_schema()
    {
        var tx = await RecordAsync("-10.00", "2026-07-03", "no-plan-spend");
        _ = tx;

        var result = await GetEvidenceAsync(Period(2026, 7));
        Assert.True(result.IsSuccess, result.ErrorCode);
        var evidence = result.Value!.Evidence;

        Assert.Equal(BudgetInsightPlanState.NoBudgetPlan, evidence.PlanState);
        Assert.Null(evidence.Revision);
        Assert.Null(evidence.Position);
        Assert.Null(evidence.CalculationSchemaVersion);
        Assert.Equal(1_000, evidence.BudgetActualTotalMinorUnits);
        Assert.Single(evidence.ActualMembers);
        Assert.Equal(
            evidence.BudgetActualTotalMinorUnits,
            BudgetInsightEvidenceBinding.CheckedMemberSum(evidence.ActualMembers));
        Assert.False(string.IsNullOrWhiteSpace(evidence.Ledger.SnapshotId));
        Assert.False(string.IsNullOrWhiteSpace(evidence.BindingFingerprint));
    }

    // FR-BUDGET-INSIGHTS-PROJECTION / NoActiveBudgetPlanRevision
    [Fact]
    public async Task NoActiveBudgetPlanRevision_returns_actuals_without_plan_or_position()
    {
        var cat = await CreateCategoryAsync("DraftOnlyCat");
        await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 500)], "draft only");
        var tx = await RecordAsync("-7.50", "2026-07-04", "draft-period-spend");
        await AssignCategoryAsync(tx.TransactionId, cat.CategoryId);

        var result = await GetEvidenceAsync(Period(2026, 7));
        Assert.True(result.IsSuccess, result.ErrorCode);
        var evidence = result.Value!.Evidence;

        Assert.Equal(BudgetInsightPlanState.NoActiveBudgetPlanRevision, evidence.PlanState);
        Assert.Null(evidence.Revision);
        Assert.Null(evidence.Position);
        Assert.Null(evidence.CalculationSchemaVersion);
        Assert.Equal(750, evidence.BudgetActualTotalMinorUnits);
        Assert.Single(evidence.ActualMembers);
        Assert.Equal(
            evidence.BudgetActualTotalMinorUnits,
            BudgetInsightEvidenceBinding.CheckedMemberSum(evidence.ActualMembers));
    }

    // ── Owner read parity ────────────────────────────────────────────────────

    // FR-BUDGET-INSIGHTS-PROJECTION / schema parity with owner position + revision
    [Fact]
    public async Task BoundRevision_matches_owner_revision_and_position_reads()
    {
        var cat = await CreateCategoryAsync("ParityCat");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 8_000)], "parity");
        await ActivateAsync(draft.Value!.Revision.RevisionId, "act");
        var tx = await RecordAsync("-12.00", "2026-07-06", "parity-tx");
        await AssignCategoryAsync(tx.TransactionId, cat.CategoryId);

        var evidenceResult = await GetEvidenceAsync(Period(2026, 7));
        Assert.True(evidenceResult.IsSuccess, evidenceResult.ErrorCode);
        var evidence = evidenceResult.Value!.Evidence;

        var revisionResult = await revisionQuery.HandleAsync(
            new GetBudgetPlanRevisionInput(
                BudgetOperationIds.ContractVersion,
                evidence.Revision!.RevisionId),
            actor,
            CancellationToken.None);
        Assert.True(revisionResult.IsSuccess, revisionResult.ErrorCode);

        var positionResult = await positionQuery.HandleAsync(
            new GetBudgetPositionInput(BudgetOperationIds.ContractVersion, Period(2026, 7), null),
            actor,
            CancellationToken.None);
        Assert.True(positionResult.IsSuccess, positionResult.ErrorCode);

        Assert.Equal(revisionResult.Value!.RevisionId, evidence.Revision.RevisionId);
        Assert.Equal(revisionResult.Value.PayloadHash, evidence.Revision.PayloadHash);
        Assert.Equal(revisionResult.Value.PlannedTotalMinorUnits, evidence.Revision.PlannedTotalMinorUnits);
        Assert.Equal(revisionResult.Value.Entries.Count, evidence.Revision.Entries.Count);

        Assert.Equal(positionResult.Value!.Position!.RevisionId, evidence.Position!.RevisionId);
        Assert.Equal(positionResult.Value.Position.Totals.PlannedMinorUnits, evidence.Position.Totals.PlannedMinorUnits);
        Assert.Equal(positionResult.Value.Position.Totals.ActualMinorUnits, evidence.Position.Totals.ActualMinorUnits);
        Assert.Equal(positionResult.Value.Position.Totals.RemainingMinorUnits, evidence.Position.Totals.RemainingMinorUnits);
        Assert.Equal(positionResult.Value.Position.Totals.OverMinorUnits, evidence.Position.Totals.OverMinorUnits);
        Assert.Equal(positionResult.Value.Position.CategoryPositions.Count, evidence.Position.CategoryPositions.Count);
        // Independent LEDGER captures mint distinct snapshot ids; amounts and plan identity must match.
        Assert.Equal(
            positionResult.Value.Position.CalculationSchemaVersion,
            evidence.CalculationSchemaVersion);
        Assert.Equal(evidence.Ledger.SnapshotId, evidence.Position.Ledger.SnapshotId);
    }

    // ── Single-snapshot correction race ──────────────────────────────────────

    // FR-BUDGET-INSIGHTS-PROJECTION / pre-capture correction → wholly post-correction
    [Fact]
    public async Task Correction_before_capture_yields_wholly_post_correction_result()
    {
        var cat = await CreateCategoryAsync("PreCorr");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 5_000)], "pre");
        await ActivateAsync(draft.Value!.Revision.RevisionId, "act");
        var tx = await RecordAsync("-20.00", "2026-07-09", "will-void-pre");
        await AssignCategoryAsync(tx.TransactionId, cat.CategoryId);
        await VoidAsync(tx.TransactionId);

        var result = await GetEvidenceAsync(Period(2026, 7));
        Assert.True(result.IsSuccess, result.ErrorCode);
        var evidence = result.Value!.Evidence;

        Assert.Equal(BudgetInsightPlanState.BoundRevision, evidence.PlanState);
        Assert.Equal(0, evidence.BudgetActualTotalMinorUnits);
        Assert.Equal(0, evidence.Position!.Totals.ActualMinorUnits);
        // Voided transaction is not in Active lifecycle membership.
        Assert.DoesNotContain(evidence.ActualMembers, m => m.TransactionId == tx.TransactionId);
    }

    // FR-BUDGET-INSIGHTS-PROJECTION / post-capture correction → first result pre, second post
    [Fact]
    public async Task Correction_after_capture_leaves_prior_result_wholly_pre_correction()
    {
        var cat = await CreateCategoryAsync("PostCorr");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 5_000)], "post");
        await ActivateAsync(draft.Value!.Revision.RevisionId, "act");
        var tx = await RecordAsync("-20.00", "2026-07-09", "will-void-post");
        await AssignCategoryAsync(tx.TransactionId, cat.CategoryId);

        var first = await GetEvidenceAsync(Period(2026, 7));
        Assert.True(first.IsSuccess, first.ErrorCode);
        Assert.Equal(2_000, first.Value!.Evidence.BudgetActualTotalMinorUnits);
        Assert.Equal(2_000, first.Value.Evidence.Position!.Totals.ActualMinorUnits);
        var firstSnapshot = first.Value.Evidence.Ledger.SnapshotId;
        var firstFingerprint = first.Value.Evidence.Ledger.StoreGenerationFingerprint;
        var firstBinding = first.Value.Evidence.BindingFingerprint;

        await VoidAsync(tx.TransactionId);

        // Prior result remains wholly pre-correction — immutable after capture.
        Assert.Equal(2_000, first.Value.Evidence.BudgetActualTotalMinorUnits);
        Assert.Equal(firstSnapshot, first.Value.Evidence.Ledger.SnapshotId);
        Assert.Equal(firstFingerprint, first.Value.Evidence.Ledger.StoreGenerationFingerprint);
        Assert.Equal(firstBinding, first.Value.Evidence.BindingFingerprint);
        Assert.Contains(first.Value.Evidence.ActualMembers, m => m.TransactionId == tx.TransactionId);

        var second = await GetEvidenceAsync(Period(2026, 7));
        Assert.True(second.IsSuccess, second.ErrorCode);
        Assert.Equal(0, second.Value!.Evidence.BudgetActualTotalMinorUnits);
        Assert.NotEqual(firstSnapshot, second.Value.Evidence.Ledger.SnapshotId);
        Assert.NotEqual(firstBinding, second.Value.Evidence.BindingFingerprint);
    }

    // FR-BUDGET-INSIGHTS-PROJECTION / unprovable binding → SourceStateChanged
    [Fact]
    public void Incomplete_ledger_snapshot_maps_to_SourceStateChanged_not_partial_output()
    {
        // Pure mapper path: missing generation fingerprint cannot prove one binding.
        var incomplete = new ActualsQueryResult(
            SnapshotId: "snap",
            ExpiresAt: "2099-01-01T00:00:00Z",
            TotalCount: 0,
            Items: [],
            Totals: new ActualsTotalsResult("0", "0", "0"),
            Groups: [],
            Cursor: null,
            LedgerContractVersion: ActualsContractVersions.Current,
            StoreGenerationFingerprint: null);

        Assert.Null(BudgetContractMapper.TryMapLedgerSnapshot(incomplete, out var error));
        Assert.Equal(BudgetErrors.Integrity, error);

        // Composition error path for generation races.
        Assert.Equal(
            BudgetErrors.SourceStateChanged,
            BudgetContractMapper.MapLedgerCompositionError(
                new ProcessError(ActualsErrors.GenerationMismatch, "conflict", "gen")));
        Assert.Equal(
            BudgetErrors.SourceStateChanged,
            BudgetContractMapper.MapLedgerCompositionError(
                new ProcessError(ActualsErrors.SnapshotExpired, "conflict", "exp")));
    }

    // ── Bound-position and absent-total reconciliation ────────────────────────

    // FR-BUDGET-INSIGHTS-PROJECTION / bound member-to-position reconciliation
    [Fact]
    public async Task BoundRevision_members_reconcile_exactly_once_to_position_total()
    {
        var catA = await CreateCategoryAsync("RecA");
        var catB = await CreateCategoryAsync("RecB");
        var draft = await CreateDraftAsync(
            Period(2026, 7),
            [Entry(catA.CategoryId, 1_000), Entry(catB.CategoryId, 2_000)],
            "rec");
        await ActivateAsync(draft.Value!.Revision.RevisionId, "act");
        var t1 = await RecordAsync("-5.00", "2026-07-01", "rec-1");
        var t2 = await RecordAsync("-3.00", "2026-07-02", "rec-2");
        var t3 = await RecordAsync("-1.00", "2026-07-03", "rec-uncat");
        await AssignCategoryAsync(t1.TransactionId, catA.CategoryId);
        await AssignCategoryAsync(t2.TransactionId, catB.CategoryId);

        var result = await GetEvidenceAsync(Period(2026, 7));
        Assert.True(result.IsSuccess, result.ErrorCode);
        var evidence = result.Value!.Evidence;

        Assert.Equal(3, evidence.ActualMembers.Count);
        Assert.Equal(
            evidence.ActualMembers.Select(m => m.TransactionId).Distinct().Count(),
            evidence.ActualMembers.Count);
        Assert.Equal(
            evidence.BudgetActualTotalMinorUnits,
            BudgetInsightEvidenceBinding.CheckedMemberSum(evidence.ActualMembers));
        Assert.Equal(evidence.BudgetActualTotalMinorUnits, evidence.Position!.Totals.ActualMinorUnits);
        Assert.Equal(900, evidence.BudgetActualTotalMinorUnits);

        // Binding fingerprint recomputes identically.
        var recomputed = BudgetInsightEvidenceBinding.ComputeBindingFingerprint(
            evidence.PlanState,
            evidence.Revision!.RevisionId,
            evidence.CalculationSchemaVersion,
            evidence.Ledger,
            evidence.BudgetActualTotalMinorUnits,
            evidence.ActualMembers);
        Assert.Equal(evidence.BindingFingerprint, recomputed);
        _ = t3;
    }

    // FR-BUDGET-INSIGHTS-PROJECTION / absent total reconciliation
    [Fact]
    public async Task Plan_absence_members_sum_equals_ledger_total_without_position()
    {
        await RecordAsync("-4.00", "2026-07-10", "abs-1");
        await RecordAsync("-6.00", "2026-07-11", "abs-2");

        var result = await GetEvidenceAsync(Period(2026, 7));
        Assert.True(result.IsSuccess, result.ErrorCode);
        var evidence = result.Value!.Evidence;

        Assert.Equal(BudgetInsightPlanState.NoBudgetPlan, evidence.PlanState);
        Assert.Null(evidence.Position);
        Assert.Null(evidence.Revision);
        Assert.Null(evidence.CalculationSchemaVersion);
        Assert.Equal(2, evidence.ActualMembers.Count);
        Assert.Equal(1_000, evidence.BudgetActualTotalMinorUnits);
        Assert.Equal(
            evidence.BudgetActualTotalMinorUnits,
            BudgetInsightEvidenceBinding.CheckedMemberSum(evidence.ActualMembers));
    }

    // ── Limits ───────────────────────────────────────────────────────────────

    // FR-BUDGET-INSIGHTS-PROJECTION / one-over-limit fails before financial output
    [Fact]
    public async Task MemberLimit_one_over_max_fails_as_ResourceLimit_before_financial_output()
    {
        var result = await evidenceQuery.HandleAsync(
            new GetBudgetInsightEvidenceInput(
                BudgetOperationIds.ContractVersion,
                Period(2026, 7),
                RevisionId: null,
                MemberLimit: GetBudgetInsightEvidenceQuery.MaxMemberLimit + 1),
            actor,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BudgetErrors.ResourceLimit, result.ErrorCode);
        Assert.Null(result.Value);
    }

    // FR-BUDGET-INSIGHTS-PROJECTION / zero and negative limits
    [Fact]
    public async Task MemberLimit_zero_or_negative_fails_as_ResourceLimit()
    {
        var zero = await evidenceQuery.HandleAsync(
            new GetBudgetInsightEvidenceInput(
                BudgetOperationIds.ContractVersion, Period(2026, 7), null, 0),
            actor,
            CancellationToken.None);
        var negative = await evidenceQuery.HandleAsync(
            new GetBudgetInsightEvidenceInput(
                BudgetOperationIds.ContractVersion, Period(2026, 7), null, -1),
            actor,
            CancellationToken.None);

        Assert.Equal(BudgetErrors.ResourceLimit, zero.ErrorCode);
        Assert.Equal(BudgetErrors.ResourceLimit, negative.ErrorCode);
    }

    // FR-BUDGET-INSIGHTS-PROJECTION / complete set exceeding requested limit
    [Fact]
    public async Task Complete_member_set_exceeding_requested_limit_fails_without_truncation()
    {
        await RecordAsync("-1.00", "2026-07-01", "lim-1");
        await RecordAsync("-2.00", "2026-07-02", "lim-2");

        var result = await evidenceQuery.HandleAsync(
            new GetBudgetInsightEvidenceInput(
                BudgetOperationIds.ContractVersion,
                Period(2026, 7),
                null,
                MemberLimit: 1),
            actor,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BudgetErrors.ResourceLimit, result.ErrorCode);
        Assert.Null(result.Value);
    }

    // ── Compatibility ────────────────────────────────────────────────────────

    // FR-BUDGET-INSIGHTS-PROJECTION / unsupported version
    [Fact]
    public async Task Unsupported_contract_version_fails_before_financial_output()
    {
        var result = await evidenceQuery.HandleAsync(
            new GetBudgetInsightEvidenceInput(
                "9.9",
                Period(2026, 7),
                null,
                null),
            actor,
            CancellationToken.None);

        Assert.Equal(BudgetErrors.UnsupportedVersion, result.ErrorCode);
        Assert.Null(result.Value);
    }

    // FR-BUDGET-INSIGHTS-PROJECTION / missing actor
    [Fact]
    public async Task Missing_actor_fails_closed()
    {
        var result = await evidenceQuery.HandleAsync(
            new GetBudgetInsightEvidenceInput(
                BudgetOperationIds.ContractVersion, Period(2026, 7), null, null),
            actor: null,
            CancellationToken.None);

        Assert.Equal(BudgetErrors.ActorRequired, result.ErrorCode);
    }

    // FR-BUDGET-INSIGHTS-PROJECTION / invalid period
    [Fact]
    public async Task Invalid_period_fails_before_ledger()
    {
        var result = await evidenceQuery.HandleAsync(
            new GetBudgetInsightEvidenceInput(
                BudgetOperationIds.ContractVersion,
                new BudgetPeriodInput(2026, 13, "ZAR"),
                null,
                null),
            actor,
            CancellationToken.None);

        Assert.Equal(BudgetErrors.InvalidPeriod, result.ErrorCode);
    }

    // FR-BUDGET-INSIGHTS-PROJECTION / explicit revision not found
    [Fact]
    public async Task Explicit_unknown_revision_is_RevisionNotFound()
    {
        var result = await GetEvidenceAsync(Period(2026, 7), "01HZZZZZZZZZZZZZZZZZZZZZZZ");
        Assert.Equal(BudgetErrors.RevisionNotFound, result.ErrorCode);
    }

    // ── Calculator exclusion for absence states ──────────────────────────────

    // FR-BUDGET-INSIGHTS-PROJECTION / calculator exclusion
    [Fact]
    public async Task Absence_states_omit_calculation_schema_and_position()
    {
        // NoBudgetPlan
        var noPlan = await GetEvidenceAsync(Period(2026, 7));
        Assert.True(noPlan.IsSuccess, noPlan.ErrorCode);
        Assert.Null(noPlan.Value!.Evidence.Position);
        Assert.Null(noPlan.Value.Evidence.CalculationSchemaVersion);

        // NoActiveBudgetPlanRevision
        var cat = await CreateCategoryAsync("CalcExcl");
        await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 10)], "excl");
        var noActive = await GetEvidenceAsync(Period(2026, 7));
        Assert.True(noActive.IsSuccess, noActive.ErrorCode);
        Assert.Equal(BudgetInsightPlanState.NoActiveBudgetPlanRevision, noActive.Value!.Evidence.PlanState);
        Assert.Null(noActive.Value.Evidence.Position);
        Assert.Null(noActive.Value.Evidence.CalculationSchemaVersion);
        Assert.Null(noActive.Value.Evidence.Revision);
    }

    // ── Analytics exclusion ──────────────────────────────────────────────────

    // FR-BUDGET-INSIGHTS-PROJECTION / no analytics / interpretation fields
    [Fact]
    public async Task Evidence_json_excludes_analytics_and_interpretation_fields()
    {
        var cat = await CreateCategoryAsync("AnalyticFree");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 100)], "af");
        await ActivateAsync(draft.Value!.Revision.RevisionId, "act");

        var result = await GetEvidenceAsync(Period(2026, 7));
        Assert.True(result.IsSuccess, result.ErrorCode);

        var json = JsonSerializer.Serialize(
            result.Value!.Evidence,
            BudgetJsonContext.Default.BudgetInsightEvidence);

        string[] forbidden =
        [
            "pace", "forecast", "trend", "anomaly", "recommendation",
            "narrative", "alert", "reportSnapshot", "report_snapshot",
            "insightReport", "durableReport"
        ];

        foreach (var field in forbidden)
        {
            Assert.DoesNotContain($"\"{field}\"", json, StringComparison.OrdinalIgnoreCase);
        }

        // Required evidence fields are present.
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("planState", out _));
        Assert.True(doc.RootElement.TryGetProperty("actualMembers", out _));
        Assert.True(doc.RootElement.TryGetProperty("budgetActualTotalMinorUnits", out _));
        Assert.True(doc.RootElement.TryGetProperty("ledger", out _));
        Assert.True(doc.RootElement.TryGetProperty("bindingFingerprint", out _));
    }

    // ── No consumer state ────────────────────────────────────────────────────

    // FR-BUDGET-INSIGHTS-PROJECTION / no consumer-specific budget.db records
    [Fact]
    public async Task After_consumer_reads_budget_db_has_no_report_or_consumer_tables()
    {
        var cat = await CreateCategoryAsync("NoState");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 50)], "state");
        await ActivateAsync(draft.Value!.Revision.RevisionId, "act");
        await RecordAsync("-1.00", "2026-07-08", "state-tx");

        _ = await GetEvidenceAsync(Period(2026, 7));
        _ = await GetEvidenceAsync(Period(2026, 7));
        _ = await positionQuery.HandleAsync(
            new GetBudgetPositionInput(BudgetOperationIds.ContractVersion, Period(2026, 7), null),
            actor,
            CancellationToken.None);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;
        var tables = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        string[] allowed =
        [
            "budget_plan",
            "budget_plan_revision",
            "budget_plan_entry",
            "budget_lifecycle_event",
            "budget_idempotency_record"
        ];
        Assert.Equal(allowed.Order(StringComparer.Ordinal), tables.Order(StringComparer.Ordinal));

        Assert.DoesNotContain(tables, t => t.Contains("report", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(tables, t => t.Contains("recommendation", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(tables, t => t.Contains("insight", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(tables, t => t.Contains("consumer", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(tables, t => t.Contains("position", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(tables, t => t.Contains("actual", StringComparison.OrdinalIgnoreCase));

        // Known plan rows exist; no derived consumer replica tables were added.
        Assert.Equal(1, await CountBudgetAsync("SELECT COUNT(*) FROM budget_plan;"));
        Assert.True(await CountBudgetAsync("SELECT COUNT(*) FROM budget_plan_revision;") >= 1);
    }

    // FR-BUDGET-INSIGHTS-PROJECTION / explicit draft selector is BoundRevision
    [Fact]
    public async Task Explicit_draft_revision_is_BoundRevision_without_activation()
    {
        var cat = await CreateCategoryAsync("ExplicitDraft");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(cat.CategoryId, 333)], "draft-sel");

        var result = await GetEvidenceAsync(Period(2026, 7), draft.Value!.Revision.RevisionId);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.Equal(BudgetInsightPlanState.BoundRevision, result.Value!.Evidence.PlanState);
        Assert.Equal(BudgetRevisionStatus.Draft, result.Value.Evidence.Revision!.Status);
        Assert.Equal(333, result.Value.Evidence.Position!.Totals.PlannedMinorUnits);
        Assert.Equal(draft.Value.Revision.RevisionId, result.Value.Evidence.Position.RevisionId);
    }

    // FR-BUDGET-INSIGHTS-PROJECTION / binding fingerprint is deterministic
    [Fact]
    public void Binding_fingerprint_is_deterministic_over_identical_inputs()
    {
        var ledgerEvidence = new LedgerSnapshotEvidence(
            ActualsContractVersions.Current,
            "snap-1",
            "2099-01-01T00:00:00Z",
            "gen-1");
        BudgetActualMember[] members =
        [
            new(0, "tx-a", "2026-07-01", "cat-1", 100),
            new(1, "tx-b", "2026-07-02", null, -25)
        ];

        var a = BudgetInsightEvidenceBinding.ComputeBindingFingerprint(
            BudgetInsightPlanState.NoBudgetPlan,
            null,
            null,
            ledgerEvidence,
            75,
            members);
        var b = BudgetInsightEvidenceBinding.ComputeBindingFingerprint(
            BudgetInsightPlanState.NoBudgetPlan,
            null,
            null,
            ledgerEvidence,
            75,
            members);

        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
        Assert.Matches("^[0-9a-f]{64}$", a);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Task<CommandResult<GetBudgetInsightEvidenceResult>> GetEvidenceAsync(
        BudgetPeriodInput period,
        string? revisionId = null,
        int? memberLimit = null) =>
        evidenceQuery.HandleAsync(
            new GetBudgetInsightEvidenceInput(
                BudgetOperationIds.ContractVersion,
                period,
                revisionId,
                memberLimit),
            actor,
            CancellationToken.None);

    private Task<CommandResult<CreateDraftBudgetPlanResult>> CreateDraftAsync(
        BudgetPeriodInput period,
        IReadOnlyList<BudgetPlanEntryInput> entries,
        string reason,
        string? key = null) =>
        draftCommand.HandleAsync(
            new CreateDraftBudgetPlanInput(BudgetOperationIds.ContractVersion, period, entries, reason),
            actor,
            key ?? NextKey(),
            CancellationToken.None);

    private Task<CommandResult<ActivateBudgetPlanRevisionResult>> ActivateAsync(
        string revisionId,
        string reason,
        string? key = null) =>
        activateCommand.HandleAsync(
            new ActivateBudgetPlanRevisionInput(BudgetOperationIds.ContractVersion, revisionId, reason),
            actor,
            key ?? NextKey(),
            CancellationToken.None);

    private static BudgetPeriodInput Period(int year, int month) => new(year, month, "ZAR");

    private static BudgetPlanEntryInput Entry(string categoryId, long amount) => new(categoryId, amount);

    private async Task<AccountDetail> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        return await ExecuteSuccessAsync(
            "ledger.account.create",
            new CreateAccountInput(
                $"Budget Insights Bank {unique}",
                $"Primary-{unique}",
                AccountType.Cheque,
                $"****{Random.Shared.Next(1000, 9999)}",
                "ZAR"),
            NextKey(),
            LedgerJsonContext.Default.CreateAccountInput,
            LedgerJsonContext.Default.AccountDetail);
    }

    private Task<CategoryDetail> CreateCategoryAsync(string name) =>
        ExecuteSuccessAsync(
            "ledger.category.create",
            new CreateCategoryInput(name),
            NextKey(),
            LedgerJsonContext.Default.CreateCategoryInput,
            LedgerJsonContext.Default.CategoryDetail);

    private async Task<TransactionDetail> RecordAsync(string amount, string date, string description)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(description + date + amount + Guid.NewGuid().ToString("N"))));
        return await ExecuteSuccessAsync(
            "ledger.transaction.record",
            new RecordTransactionInput(
                accountId,
                amount,
                "ZAR",
                date,
                null,
                description,
                null,
                null,
                new(EvidenceKind.AgentCapture, digest, null, null, null)),
            "record-" + digest[..16],
            LedgerJsonContext.Default.RecordTransactionInput,
            LedgerJsonContext.Default.TransactionDetail);
    }

    private async Task AssignCategoryAsync(string transactionId, string categoryId) =>
        _ = await ExecuteSuccessAsync(
            "ledger.transaction.category.assign",
            new AssignCategoryInput(transactionId, categoryId, "budget-insights"),
            "cat-" + transactionId + "-" + Guid.NewGuid().ToString("N")[..6],
            LedgerJsonContext.Default.AssignCategoryInput,
            LedgerJsonContext.Default.CategoryAllocationResult);

    private async Task VoidAsync(string transactionId) =>
        _ = await ExecuteSuccessAsync(
            "ledger.transaction.void",
            new VoidTransactionInput(transactionId, "budget-insights-void"),
            "void-" + transactionId,
            TransactionCorrectionJsonContext.Default.VoidTransactionInput,
            TransactionCorrectionJsonContext.Default.TransactionCorrectionResult);

    private async Task<long> CountBudgetAsync(string sql)
    {
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private async Task<TResult> ExecuteSuccessAsync<TInput, TResult>(
        string operationId,
        TInput input,
        string? key,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TInput> inputType,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultType)
    {
        var result = await ExecuteAsync(operationId, input, key, inputType, resultType);
        Assert.True(result.IsSuccess, result.Error?.Code);
        return result.Value!;
    }

    private async Task<LedgerContractResult<TResult>> ExecuteAsync<TInput, TResult>(
        string operationId,
        TInput input,
        string? key,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TInput> inputType,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultType)
    {
        var descriptor = registry.Find(operationId)!;
        var element = JsonSerializer.SerializeToElement(input, inputType);
        var body = JsonSerializer.Serialize(
            new RequestEnvelope("1.0", actor, element, key),
            LedgerJsonContext.Default.RequestEnvelope);
        var args = descriptor.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1).Concat(["--input", "-"]).ToArray();
        var processResult = await process.RunAsync(args, body, CancellationToken.None);
        var envelope = JsonSerializer.Deserialize(processResult.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        if (processResult.ExitCode != 0)
        {
            return new(processResult.ExitCode, default, envelope.Error, processResult.Stderr);
        }

        var value = JsonSerializer.Deserialize(envelope.Result!.Value, resultType)!;
        return new(processResult.ExitCode, value, null, processResult.Stderr);
    }

    private string NextKey() => $"budget-insights-{Interlocked.Increment(ref keySeq)}";

    private static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty))).ToLowerInvariant();

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
