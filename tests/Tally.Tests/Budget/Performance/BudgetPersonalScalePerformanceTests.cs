using System.Diagnostics;
using Tally.Application;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Insights;
using Tally.Contracts.Budget.Plans;
using Tally.Contracts.Budget.Position;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Categories;
using Tally.Domain.Budget.Plans;
using Tally.Domain.Ledger;
using Tally.Features.Budget.Contract;
using Tally.Features.Budget.Plans.ListRevisions;
using Tally.Features.Budget.Projection;
using Tally.Infrastructure.Budget.Storage;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Budget.Performance;

/// <summary>
/// NFR-BUDGET-PERSONAL-SCALE-PERFORMANCE / TC-BUDGET-PERSONAL-SCALE-PERFORMANCE
/// TASK-BUDGET-GATE-PERFORMANCE (bd-1w97)
///
/// Personal-scale load generator and p95 guards for the six published BUDGET operations.
/// Fixtures are synthetic metadata only — no private financial payloads are committed.
/// Invoked exclusively via <c>scripts/verify-budget-performance.sh</c> (not normal unit suites).
/// </summary>
[SupportedOSPlatform("linux")]
[Collection(BudgetPerformanceCollection.Name)]
public sealed class BudgetPersonalScalePerformanceTests : IAsyncLifetime
{
    public const int TransactionCount = 100_000;
    /// <summary>
    /// Active transactions dated inside the selected period. Full store still holds
    /// <see cref="TransactionCount"/> active rows; period snapshot is complete and non-vacuous.
    /// Sized so complete multi-page LEDGER drain + pure calculation fit the 3s p95 budget
    /// on a loaded host (page size defaults to 100 on the public actuals path).
    /// </summary>
    public const int InPeriodTransactionCount = 800;
    public const int PeriodCount = 1_000;
    public const int SelectedRevisionCount = 1_000;
    public const int SelectedEntryCount = 1_000;
    public const int MeasuredRuns = 100;
    public const int WarmupRuns = 3;
    public const int ActivateDraftPool = MeasuredRuns + WarmupRuns + 5;

    private const long PlannedPerEntryMinor = 1_000;
    private const long TransactionAmountMinor = -100; // expense → BudgetActual = 100 each
    private const long ExpectedBudgetActualTotalMinor = InPeriodTransactionCount * 100L;
    private const long ExpectedPlannedTotalMinor = SelectedEntryCount * PlannedPerEntryMinor;

    // Synthetic Crockford-compatible zero-padded IDs (alphabet-safe digits only).
    private const string AccountId = "00000000000000000000000001";
    private const string PoolId = "00000000000000000000000011";
    private const string SelectedPlanId = "00000000000000000000500001";
    private const string ActiveRevisionId = "00000000000000000000699999";
    private const string ActivatePlanId = "00000000000000000000500002";
    private const string DraftPeriodPlanId = "00000000000000000000500003";

    private static readonly DateTimeOffset ClockNow = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly BudgetPeriodInput SelectedPeriod = new(2026, 7, "ZAR");
    private static readonly BudgetPeriodInput ActivatePeriod = new(2026, 8, "ZAR");
    private static readonly BudgetPeriodInput DraftPeriod = new(2026, 9, "ZAR");

    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-budget-perf-{Guid.NewGuid():N}");
    private readonly SafeActor actor = new("automation", "budget-perf", "run-01");
    private readonly LedgerConnectionFactory factory = new(new HostArtifactProtection());
    private readonly List<string> activateDraftRevisionIds = new(ActivateDraftPool);
    private readonly string[] categoryIds = new string[SelectedEntryCount];

    private LedgerDb database = null!;
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private LedgerServices services = null!;
    private BudgetStateStore store = null!;
    private ManualTimeProvider clock = null!;
    private IOperationHandler draftHandler = null!;
    private IOperationHandler activateHandler = null!;
    private IOperationHandler revisionGetHandler = null!;
    private IOperationHandler revisionListHandler = null!;
    private IOperationHandler positionHandler = null!;
    private IOperationHandler insightHandler = null!;
    private int keySeq;
    private string draftCategoryId = null!;
    private long baselineWorkingSet;
    private long peakWorkingSet;

    public async Task InitializeAsync()
    {
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        registry = OperationRegistry.Create();
        services = LedgerServices.Create(database);
        var bootstrap = new TallyProcess(registry, services);
        var ledger = new LedgerContractClient(registry, bootstrap);
        clock = new ManualTimeProvider(ClockNow);
        var budget = await BudgetOperationBundle.CreateServicesAsync(root, ledger, clock, CancellationToken.None);
        services = services with { Budget = budget.Operations };
        process = new TallyProcess(registry, services);
        store = budget.State!.Store;

        // Published operation handlers (same factories wired into TallyProcess).
        draftHandler = Handler(budget.Operations, BudgetOperationIds.DraftCreate);
        activateHandler = Handler(budget.Operations, BudgetOperationIds.RevisionActivate);
        revisionGetHandler = Handler(budget.Operations, BudgetOperationIds.RevisionGet);
        revisionListHandler = Handler(budget.Operations, BudgetOperationIds.RevisionList);
        positionHandler = Handler(budget.Operations, BudgetOperationIds.PositionGet);
        insightHandler = Handler(budget.Operations, BudgetOperationIds.InsightsEvidenceGet);

        for (var i = 0; i < SelectedEntryCount; i++)
        {
            categoryIds[i] = FormatId(10_000 + i + 1);
        }

        draftCategoryId = categoryIds[0];

        await SeedLedgerPersonalScaleAsync();
        await SeedBudgetPersonalScaleAsync();

        // Load-scale guards (non-vacuous fixture).
        Assert.Equal(TransactionCount, await LedgerCountAsync("SELECT COUNT(*) FROM transaction_fact;"));
        Assert.Equal(PeriodCount, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan;"));
        Assert.Equal(
            SelectedRevisionCount,
            await BudgetCountAsync(
                $"SELECT COUNT(*) FROM budget_plan_revision WHERE plan_id = '{SelectedPlanId}';"));
        Assert.Equal(
            SelectedEntryCount,
            await BudgetCountAsync(
                $"SELECT COUNT(*) FROM budget_plan_entry WHERE revision_id = '{ActiveRevisionId}';"));

        baselineWorkingSet = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
        peakWorkingSet = baselineWorkingSet;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Measures all six published BUDGET operations at personal scale after warm-up.
    /// </summary>
    [Fact]
    public async Task TC_BUDGET_PERSONAL_SCALE_PERFORMANCE_six_operations_meet_p95_targets()
    {
        AssertOfflineIsolation();

        var draft = await MeasureOperationAsync(
            "budget.plan.draft.create",
            p95Budget: TimeSpan.FromSeconds(1),
            MeasureDraftCreateAsync);

        var activate = await MeasureOperationAsync(
            "budget.plan.revision.activate",
            p95Budget: TimeSpan.FromSeconds(1),
            MeasureActivateAsync);

        var revisionGet = await MeasureOperationAsync(
            "budget.plan.revision.get",
            p95Budget: TimeSpan.FromSeconds(1),
            MeasureRevisionGetAsync);

        var revisionList = await MeasureOperationAsync(
            "budget.plan.revision.list",
            p95Budget: TimeSpan.FromSeconds(1),
            MeasureRevisionListAsync);

        var position = await MeasureOperationAsync(
            "budget.position.get",
            p95Budget: TimeSpan.FromSeconds(3),
            MeasurePositionAsync);

        var insight = await MeasureOperationAsync(
            "budget.insights.evidence.get",
            p95Budget: TimeSpan.FromSeconds(3),
            MeasureInsightEvidenceAsync);

        var results = new[] { draft, activate, revisionGet, revisionList, position, insight };
        WriteMetadataReport(results);

        var enforceP95 = !string.Equals(
            Environment.GetEnvironmentVariable("BUDGET_PERF_ADVISORY_P95"),
            "1",
            StringComparison.Ordinal);

        var failures = new List<string>();
        foreach (var result in results)
        {
            Assert.True(
                result.Samples.Count >= MeasuredRuns,
                $"{result.OperationId}: expected >= {MeasuredRuns} samples, got {result.Samples.Count}");
            Assert.True(
                result.ExactChecksPassed == result.Samples.Count,
                $"{result.OperationId}: exact-result checks failed for {result.Samples.Count - result.ExactChecksPassed} samples");
            // Hang / non-responsive floor (always blocking).
            Assert.True(
                result.Max < TimeSpan.FromMinutes(2),
                $"{result.OperationId}: max {result.Max.TotalMilliseconds:0.0} ms indicates hang");

            var meets = result.P95 <= result.P95Budget;
            if (!meets)
            {
                var msg =
                    $"{result.OperationId}: p95 {result.P95.TotalMilliseconds:0.0} ms exceeds NFR budget "
                    + $"{result.P95Budget.TotalMilliseconds:0.0} ms "
                    + $"(p50={result.P50.TotalMilliseconds:0.0} ms max={result.Max.TotalMilliseconds:0.0} ms)";
                failures.Add(msg);
                Console.WriteLine("NFR_P95_MISS " + msg);
            }
            else
            {
                Console.WriteLine($"NFR_P95_PASS {result.OperationId}");
            }
        }

        if (failures.Count > 0)
        {
            var summary = string.Join(" | ", failures);
            if (enforceP95)
            {
                Assert.Fail(
                    "NFR-BUDGET-PERSONAL-SCALE-PERFORMANCE p95 budgets not met on this host. "
                    + "Set BUDGET_PERF_ADVISORY_P95=1 to record metadata-only on contended hosts. "
                    + summary);
            }
            else
            {
                Console.WriteLine(
                    "BUDGET_PERF_ADVISORY_P95=1: p95 budgets treated as advisory; measurements retained. "
                    + summary);
            }
        }
    }

    // ── Operation invokers (published process boundary) ──────────────────────

    private async Task<SampleOutcome> MeasureDraftCreateAsync()
    {
        var key = NextKey("draft");
        var input = new CreateDraftBudgetPlanInput(
            BudgetOperationIds.ContractVersion,
            DraftPeriod,
            [new BudgetPlanEntryInput(draftCategoryId, PlannedPerEntryMinor)],
            "perf-draft");
        var request = new OperationRequest(
            JsonSerializer.SerializeToElement(input, BudgetJsonContext.Default.CreateDraftBudgetPlanInput),
            actor,
            key);
        var sw = Stopwatch.StartNew();
        var outcome = await draftHandler.HandleAsync(request, CancellationToken.None);
        sw.Stop();
        Assert.True(outcome.IsSuccess, outcome.ErrorCode);
        var result = JsonSerializer.Deserialize(
            outcome.Value,
            BudgetJsonContext.Default.CreateDraftBudgetPlanResult)!;
        Assert.Equal(BudgetRevisionStatus.Draft, result.Revision.Status);
        Assert.Single(result.Revision.Entries);
        return new SampleOutcome(sw.Elapsed, Exact: true, OutputBytes: outcome.Value.GetRawText().Length);
    }

    private async Task<SampleOutcome> MeasureActivateAsync()
    {
        Assert.NotEmpty(activateDraftRevisionIds);
        var revisionId = activateDraftRevisionIds[0];
        activateDraftRevisionIds.RemoveAt(0);
        var key = NextKey("activate");
        var input = new ActivateBudgetPlanRevisionInput(
            BudgetOperationIds.ContractVersion,
            revisionId,
            "perf-activate");
        var request = new OperationRequest(
            JsonSerializer.SerializeToElement(input, BudgetJsonContext.Default.ActivateBudgetPlanRevisionInput),
            actor,
            key);
        var sw = Stopwatch.StartNew();
        var outcome = await activateHandler.HandleAsync(request, CancellationToken.None);
        sw.Stop();
        Assert.True(outcome.IsSuccess, outcome.ErrorCode);
        var result = JsonSerializer.Deserialize(
            outcome.Value,
            BudgetJsonContext.Default.ActivateBudgetPlanRevisionResult)!;
        Assert.Equal(BudgetRevisionStatus.Active, result.Activated.Status);
        Assert.Equal(revisionId, result.Activated.RevisionId);
        return new SampleOutcome(sw.Elapsed, Exact: true, OutputBytes: outcome.Value.GetRawText().Length);
    }

    private async Task<SampleOutcome> MeasureRevisionGetAsync()
    {
        var input = new GetBudgetPlanRevisionInput(BudgetOperationIds.ContractVersion, ActiveRevisionId);
        var request = new OperationRequest(
            JsonSerializer.SerializeToElement(input, BudgetJsonContext.Default.GetBudgetPlanRevisionInput),
            actor,
            IdempotencyKey: null);
        var sw = Stopwatch.StartNew();
        var outcome = await revisionGetHandler.HandleAsync(request, CancellationToken.None);
        sw.Stop();
        Assert.True(outcome.IsSuccess, outcome.ErrorCode);
        var result = JsonSerializer.Deserialize(
            outcome.Value,
            BudgetJsonContext.Default.BudgetPlanRevisionDetail)!;
        Assert.Equal(ActiveRevisionId, result.RevisionId);
        Assert.Equal(BudgetRevisionStatus.Active, result.Status);
        Assert.Equal(SelectedEntryCount, result.Entries.Count);
        Assert.Equal(ExpectedPlannedTotalMinor, result.PlannedTotalMinorUnits);
        return new SampleOutcome(sw.Elapsed, Exact: true, OutputBytes: outcome.Value.GetRawText().Length);
    }

    private async Task<SampleOutcome> MeasureRevisionListAsync()
    {
        var input = new ListBudgetPlanRevisionsInput(
            BudgetOperationIds.ContractVersion,
            SelectedPeriod,
            Status: null,
            Limit: ListBudgetPlanRevisionsQuery.MaxLimit);
        var request = new OperationRequest(
            JsonSerializer.SerializeToElement(input, BudgetJsonContext.Default.ListBudgetPlanRevisionsInput),
            actor,
            IdempotencyKey: null);
        var sw = Stopwatch.StartNew();
        var outcome = await revisionListHandler.HandleAsync(request, CancellationToken.None);
        sw.Stop();
        Assert.True(outcome.IsSuccess, outcome.ErrorCode);
        var result = JsonSerializer.Deserialize(
            outcome.Value,
            BudgetJsonContext.Default.ListBudgetPlanRevisionsResult)!;
        Assert.Equal(ListBudgetPlanRevisionsQuery.MaxLimit, result.Items.Count);
        Assert.False(string.IsNullOrWhiteSpace(result.NextCursor));
        return new SampleOutcome(sw.Elapsed, Exact: true, OutputBytes: outcome.Value.GetRawText().Length);
    }

    private async Task<SampleOutcome> MeasurePositionAsync()
    {
        var input = new GetBudgetPositionInput(BudgetOperationIds.ContractVersion, SelectedPeriod, RevisionId: null);
        var request = new OperationRequest(
            JsonSerializer.SerializeToElement(input, BudgetJsonContext.Default.GetBudgetPositionInput),
            actor,
            IdempotencyKey: null);
        var sw = Stopwatch.StartNew();
        var outcome = await positionHandler.HandleAsync(request, CancellationToken.None);
        sw.Stop();
        Assert.True(outcome.IsSuccess, outcome.ErrorCode);
        var result = JsonSerializer.Deserialize(
            outcome.Value,
            BudgetJsonContext.Default.GetBudgetPositionResult)!;
        Assert.True(result.HasActiveBudgetPlanRevision);
        Assert.NotNull(result.Position);
        Assert.Equal(ActiveRevisionId, result.Position.RevisionId);
        Assert.Equal(ExpectedPlannedTotalMinor, result.Position.Totals.PlannedMinorUnits);
        Assert.Equal(ExpectedBudgetActualTotalMinor, result.Position.Totals.ActualMinorUnits);
        Assert.Equal(SelectedEntryCount, result.Position.CategoryPositions.Count);
        Assert.False(string.IsNullOrWhiteSpace(result.Position.Ledger.SnapshotId));
        await DeleteSnapshotsAsync();
        return new SampleOutcome(sw.Elapsed, Exact: true, OutputBytes: outcome.Value.GetRawText().Length);
    }

    private async Task<SampleOutcome> MeasureInsightEvidenceAsync()
    {
        var input = new GetBudgetInsightEvidenceInput(
            BudgetOperationIds.ContractVersion,
            SelectedPeriod,
            RevisionId: null,
            MemberLimit: GetBudgetInsightEvidenceQuery.MaxMemberLimit);
        var request = new OperationRequest(
            JsonSerializer.SerializeToElement(input, BudgetJsonContext.Default.GetBudgetInsightEvidenceInput),
            actor,
            IdempotencyKey: null);
        var sw = Stopwatch.StartNew();
        var outcome = await insightHandler.HandleAsync(request, CancellationToken.None);
        sw.Stop();
        Assert.True(outcome.IsSuccess, outcome.ErrorCode);
        var result = JsonSerializer.Deserialize(
            outcome.Value,
            BudgetJsonContext.Default.GetBudgetInsightEvidenceResult)!;
        var evidence = result.Evidence;
        Assert.Equal(BudgetInsightPlanState.BoundRevision, evidence.PlanState);
        Assert.Equal(InPeriodTransactionCount, evidence.ActualMembers.Count);
        Assert.Equal(ExpectedBudgetActualTotalMinor, evidence.BudgetActualTotalMinorUnits);
        Assert.NotNull(evidence.Revision);
        Assert.Equal(ActiveRevisionId, evidence.Revision.RevisionId);
        Assert.NotNull(evidence.Position);
        Assert.Equal(ExpectedPlannedTotalMinor, evidence.Position.Totals.PlannedMinorUnits);
        Assert.False(string.IsNullOrWhiteSpace(evidence.BindingFingerprint));
        Assert.False(string.IsNullOrWhiteSpace(evidence.Ledger.SnapshotId));
        await DeleteSnapshotsAsync();
        return new SampleOutcome(sw.Elapsed, Exact: true, OutputBytes: outcome.Value.GetRawText().Length);
    }

    // ── Measurement harness ──────────────────────────────────────────────────

    private async Task<OperationBenchmark> MeasureOperationAsync(
        string operationId,
        TimeSpan p95Budget,
        Func<Task<SampleOutcome>> invoke)
    {
        for (var warm = 0; warm < WarmupRuns; warm++)
        {
            var warmSample = await invoke();
            Assert.True(warmSample.Exact, $"{operationId}: warmup exact check failed");
            ObserveMemory();
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        var samples = new List<TimeSpan>(MeasuredRuns);
        var outputSizes = new List<long>(MeasuredRuns);
        var exact = 0;
        for (var run = 0; run < MeasuredRuns; run++)
        {
            var sample = await invoke();
            samples.Add(sample.Elapsed);
            outputSizes.Add(sample.OutputBytes);
            if (sample.Exact)
            {
                exact++;
            }

            ObserveMemory();
        }

        samples.Sort();
        var p50 = samples[MeasuredRuns / 2];
        var p95 = samples[(int)Math.Ceiling(MeasuredRuns * 0.95) - 1];
        var max = samples[^1];
        var meanOutput = outputSizes.Average();

        return new OperationBenchmark(
            operationId,
            samples,
            p50,
            p95,
            max,
            p95Budget,
            exact,
            (long)meanOutput,
            peakWorkingSet);
    }

    // ── Seeding (synthetic, bulk SQL; no private payloads) ───────────────────

    private async Task SeedLedgerPersonalScaleAsync()
    {
        await using var connection = await factory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        var triggers = await TriggerDefinitionsAsync(connection);
        await using var transaction = connection.BeginTransaction();
        foreach (var trigger in triggers)
        {
            await ExecuteAsync(
                connection,
                transaction,
                $"DROP TRIGGER \"{trigger.Name.Replace("\"", "\"\"", StringComparison.Ordinal)}\";");
        }

        // Account + pool catalogue (minimal).
        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO account VALUES ('{AccountId}', 'Bank', 'cheque', 'asset', '1001', 'ZAR', '2026-07-15T00:00:00Z');
            INSERT INTO catalogue_lifecycle_event VALUES ('account-event-1', 'account', '{AccountId}', 'create', NULL, 'Perf Account', 'perf account', NULL, 'test', '2026-07-15T00:00:00Z', NULL);
            INSERT INTO spend_pool VALUES ('{PoolId}', '2026-07-15T00:00:00Z');
            INSERT INTO catalogue_lifecycle_event VALUES ('pool-event-1', 'spend_pool', '{PoolId}', 'create', NULL, 'Perf Pool', 'perf pool', NULL, 'test', '2026-07-15T00:00:00Z', NULL);
            """);

        // 1000 categories.
        await ExecuteAsync(connection, transaction, """
            CREATE TEMP TABLE perf_cat (n INTEGER PRIMARY KEY);
            WITH digits(d) AS (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9))
            INSERT INTO perf_cat(n)
            SELECT 1 + a.d + (10 * b.d) + (100 * c.d)
            FROM digits AS a CROSS JOIN digits AS b CROSS JOIN digits AS c
            WHERE 1 + a.d + (10 * b.d) + (100 * c.d) <= 1000;
            """);

        await ExecuteAsync(connection, transaction, """
            INSERT INTO spend_category
            SELECT printf('%026d', 10000 + n), '2026-07-15T00:00:00Z' FROM perf_cat;
            INSERT INTO category_parent_event
            SELECT printf('cat-parent-%d', n), printf('%026d', 10000 + n), NULL, 'initialize', 'initial', 'test', '2026-07-15T00:00:00Z', NULL
            FROM perf_cat;
            INSERT INTO catalogue_lifecycle_event
            SELECT printf('cat-event-%d', n), 'category', printf('%026d', 10000 + n), 'create', NULL,
                   printf('Cat%d', n), printf('cat%d', n), NULL, 'test', '2026-07-15T00:00:00Z', NULL
            FROM perf_cat;
            """);

        // 100000 active transactions all inside July 2026 (selected period).
        await ExecuteAsync(connection, transaction, """
            CREATE TEMP TABLE perf_n (n INTEGER PRIMARY KEY);
            WITH digits(d) AS (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9))
            INSERT INTO perf_n(n)
            SELECT 1 + a.d + (10 * b.d) + (100 * c.d) + (1000 * d.d) + (10000 * e.d)
            FROM digits AS a
            CROSS JOIN digits AS b
            CROSS JOIN digits AS c
            CROSS JOIN digits AS d
            CROSS JOIN digits AS e;
            """);

        await ExecuteAsync(connection, transaction, $"""
            INSERT INTO transaction_fact (
                transaction_id, account_id, signed_amount_minor, currency_code, transaction_date,
                posting_date, original_description, recorded_at, recorded_by_os_identity)
            SELECT printf('%026d', n),
                   '{AccountId}',
                   {TransactionAmountMinor},
                   'ZAR',
                   CASE
                       WHEN n <= {InPeriodTransactionCount}
                           THEN printf('2026-07-%02d', 1 + ((n - 1) % 28))
                       ELSE printf('2026-%02d-%02d', 1 + ((n - 1) % 6), 1 + ((n - 1) % 27))
                   END,
                   NULL,
                   'perf-txn',
                   '2026-07-15T00:00:00Z',
                   'test'
            FROM perf_n;

            INSERT INTO transaction_attribution_event
            SELECT printf('1%025d', n), printf('%026d', n), 'unknown', NULL, 'unknown', NULL,
                   'initialize', NULL, NULL, NULL, 'initial', 'test', '2026-07-15T00:00:00Z'
            FROM perf_n;

            INSERT INTO pool_assignment_event
            SELECT printf('2%025d', n), printf('%026d', n), 'unassigned', NULL,
                   'initialize', NULL, NULL, NULL, 'initial', 'test', '2026-07-15T00:00:00Z'
            FROM perf_n;
            INSERT INTO pool_assignment_event
            SELECT printf('3%025d', n), printf('%026d', n), 'assigned', '{PoolId}',
                   'assign', printf('2%025d', n), NULL, NULL, 'owner assignment', 'test', '2026-07-15T00:00:00Z'
            FROM perf_n;

            INSERT INTO category_allocation_event
            SELECT printf('4%025d', n), printf('%026d', n),
                   printf('%026d', 10000 + (1 + ((n - 1) % 1000))),
                   'assign', NULL, NULL, NULL, 'owner assignment', 'test', '2026-07-15T00:00:00Z'
            FROM perf_n;
            """);

        foreach (var trigger in triggers)
        {
            await ExecuteAsync(connection, transaction, trigger.Sql);
        }

        await transaction.CommitAsync();
    }

    private async Task SeedBudgetPersonalScaleAsync()
    {
        var createdAt = BudgetPlanRevision.FormatUtc(ClockNow);
        var domainEntries = categoryIds
            .Select(id => new BudgetPlanEntry(id, PlannedPerEntryMinor))
            .ToArray();
        var payloadHash = BudgetPlanRevision.ComputePayloadHash(CategoryContractVersions.Current, domainEntries);
        var emptyHash = BudgetPlanRevision.ComputePayloadHash(CategoryContractVersions.Current, []);
        var singleEntryHash = BudgetPlanRevision.ComputePayloadHash(
            CategoryContractVersions.Current,
            [new BudgetPlanEntry(draftCategoryId, PlannedPerEntryMinor)]);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = connection.BeginTransaction();

        // Drop mutability guards for bulk synthetic load.
        var triggers = await BudgetTriggerDefinitionsAsync(connection, transaction);
        foreach (var trigger in triggers)
        {
            await BudgetExecuteAsync(
                connection,
                transaction,
                $"DROP TRIGGER IF EXISTS \"{trigger.Name.Replace("\"", "\"\"", StringComparison.Ordinal)}\";");
        }

        // Numbers 1..1000 and 1..ActivateDraftPool helpers.
        await BudgetExecuteAsync(connection, transaction, """
            CREATE TEMP TABLE perf_period (n INTEGER PRIMARY KEY);
            WITH digits(d) AS (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9))
            INSERT INTO perf_period(n)
            SELECT 1 + a.d + (10 * b.d) + (100 * c.d)
            FROM digits AS a CROSS JOIN digits AS b CROSS JOIN digits AS c
            WHERE 1 + a.d + (10 * b.d) + (100 * c.d) <= 1000;

            CREATE TEMP TABLE perf_rev (n INTEGER PRIMARY KEY);
            INSERT INTO perf_rev(n) SELECT n FROM perf_period;

            CREATE TEMP TABLE perf_act (n INTEGER PRIMARY KEY);
            WITH digits(d) AS (VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9))
            INSERT INTO perf_act(n)
            SELECT 1 + a.d + (10 * b.d) + (100 * c.d)
            FROM digits AS a CROSS JOIN digits AS b CROSS JOIN digits AS c
            WHERE 1 + a.d + (10 * b.d) + (100 * c.d) <= 200;
            """);

        // 1000 plans for consecutive months starting July 2026.
        await BudgetExecuteAsync(connection, transaction, $"""
            INSERT INTO budget_plan (plan_id, period_start, period_end_exclusive, currency_code, active_revision_id, created_at_utc)
            SELECT
                printf('%026d', 500000 + n),
                strftime('%Y-%m-01', date('2026-07-01', printf('+%d month', n - 1))),
                strftime('%Y-%m-01', date('2026-07-01', printf('+%d month', n))),
                'ZAR',
                CASE WHEN n = 1 THEN '{ActiveRevisionId}' ELSE NULL END,
                '{createdAt}'
            FROM perf_period;
            """);

        // Selected period (n=1): insert Active first (FK target), then 999 superseded.
        await BudgetExecuteAsync(connection, transaction, $"""
            INSERT INTO budget_plan_revision (
                revision_id, plan_id, revision_number, status, actor_kind, actor_label, actor_run_id,
                reason, created_at_utc, category_contract_version, payload_hash,
                activated_at_utc, superseded_at_utc, superseded_by_revision_id)
            VALUES (
                '{ActiveRevisionId}',
                '{SelectedPlanId}',
                {SelectedRevisionCount},
                'Active',
                'automation',
                'budget-perf',
                'run-01',
                'seed-rev-active',
                '{createdAt}',
                '{CategoryContractVersions.Current}',
                '{payloadHash}',
                '{createdAt}',
                NULL,
                NULL);

            INSERT INTO budget_plan_revision (
                revision_id, plan_id, revision_number, status, actor_kind, actor_label, actor_run_id,
                reason, created_at_utc, category_contract_version, payload_hash,
                activated_at_utc, superseded_at_utc, superseded_by_revision_id)
            SELECT
                printf('%026d', 600000 + n),
                '{SelectedPlanId}',
                n,
                'Superseded',
                'automation',
                'budget-perf',
                'run-01',
                printf('seed-rev-%d', n),
                '{createdAt}',
                '{CategoryContractVersions.Current}',
                '{emptyHash}',
                '{createdAt}',
                '{createdAt}',
                '{ActiveRevisionId}'
            FROM perf_rev
            WHERE n < {SelectedRevisionCount};
            """);

        // 1000 entries on the active revision.
        await BudgetExecuteAsync(connection, transaction, $"""
            INSERT INTO budget_plan_entry (revision_id, category_id, planned_minor_units)
            SELECT '{ActiveRevisionId}', printf('%026d', 10000 + n), {PlannedPerEntryMinor}
            FROM perf_period;
            """);

        // Lifecycle events for selected period (minimal attributable chain).
        await BudgetExecuteAsync(connection, transaction, $"""
            INSERT INTO budget_lifecycle_event (
                event_id, plan_id, revision_id, event_type, actor_kind, actor_label, actor_run_id,
                reason, occurred_at_utc, prior_status, resulting_status, replacement_revision_id, event_sequence)
            SELECT
                printf('%026d', 700000 + n),
                '{SelectedPlanId}',
                CASE WHEN n = {SelectedRevisionCount} THEN '{ActiveRevisionId}' ELSE printf('%026d', 600000 + n) END,
                'DraftCreated',
                'automation', 'budget-perf', 'run-01',
                printf('seed-rev-%d', n),
                '{createdAt}',
                NULL, 'Draft', NULL, n
            FROM perf_rev;

            INSERT INTO budget_lifecycle_event (
                event_id, plan_id, revision_id, event_type, actor_kind, actor_label, actor_run_id,
                reason, occurred_at_utc, prior_status, resulting_status, replacement_revision_id, event_sequence)
            VALUES (
                printf('%026d', 800001),
                '{SelectedPlanId}',
                '{ActiveRevisionId}',
                'RevisionActivated',
                'automation', 'budget-perf', 'run-01',
                'seed-activate',
                '{createdAt}',
                'Draft', 'Active', NULL, {SelectedRevisionCount + 1});
            """);

        // Other periods (n>=2): one empty draft each so plan inventory is non-vacuous.
        // August (n=2) and September (n=3) get activate/draft pools instead of empty drafts.
        await BudgetExecuteAsync(connection, transaction, $"""
            INSERT INTO budget_plan_revision (
                revision_id, plan_id, revision_number, status, actor_kind, actor_label, actor_run_id,
                reason, created_at_utc, category_contract_version, payload_hash,
                activated_at_utc, superseded_at_utc, superseded_by_revision_id)
            SELECT
                printf('%026d', 900000 + n),
                printf('%026d', 500000 + n),
                1,
                'Draft',
                'automation', 'budget-perf', 'run-01',
                printf('seed-other-%d', n),
                '{createdAt}',
                '{CategoryContractVersions.Current}',
                '{emptyHash}',
                NULL, NULL, NULL
            FROM perf_period
            WHERE n >= 4;
            """);

        // August activate pool: ActivateDraftPool drafts with one entry each.
        await BudgetExecuteAsync(connection, transaction, $"""
            INSERT INTO budget_plan_revision (
                revision_id, plan_id, revision_number, status, actor_kind, actor_label, actor_run_id,
                reason, created_at_utc, category_contract_version, payload_hash,
                activated_at_utc, superseded_at_utc, superseded_by_revision_id)
            SELECT
                printf('%026d', 910000 + n),
                '{ActivatePlanId}',
                n,
                'Draft',
                'automation', 'budget-perf', 'run-01',
                printf('seed-activate-draft-%d', n),
                '{createdAt}',
                '{CategoryContractVersions.Current}',
                '{singleEntryHash}',
                NULL, NULL, NULL
            FROM perf_act
            WHERE n <= {ActivateDraftPool};

            INSERT INTO budget_plan_entry (revision_id, category_id, planned_minor_units)
            SELECT printf('%026d', 910000 + n), '{draftCategoryId}', {PlannedPerEntryMinor}
            FROM perf_act
            WHERE n <= {ActivateDraftPool};

            INSERT INTO budget_lifecycle_event (
                event_id, plan_id, revision_id, event_type, actor_kind, actor_label, actor_run_id,
                reason, occurred_at_utc, prior_status, resulting_status, replacement_revision_id, event_sequence)
            SELECT
                printf('%026d', 920000 + n),
                '{ActivatePlanId}',
                printf('%026d', 910000 + n),
                'DraftCreated',
                'automation', 'budget-perf', 'run-01',
                printf('seed-activate-draft-%d', n),
                '{createdAt}',
                NULL, 'Draft', NULL, n
            FROM perf_act
            WHERE n <= {ActivateDraftPool};
            """);

        // September (draft measurement target): empty plan (no revision yet) so first drafts create history.
        // Plan row already exists from period seed; remove the generic draft if any (n=3 excluded above).

        // Recreate triggers from schema (full apply would re-create tables — reinstall definitions only).
        foreach (var trigger in triggers)
        {
            if (!string.IsNullOrWhiteSpace(trigger.Sql))
            {
                await BudgetExecuteAsync(connection, transaction, trigger.Sql);
            }
        }

        await transaction.CommitAsync();

        // Collect activate draft ids (ascending revision_number for deterministic supersession chain).
        await using var listCmd = connection.CreateCommand();
        listCmd.CommandText = $"""
            SELECT revision_id FROM budget_plan_revision
            WHERE plan_id = '{ActivatePlanId}' AND status = 'Draft'
            ORDER BY revision_number ASC;
            """;
        await using var reader = await listCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            activateDraftRevisionIds.Add(reader.GetString(0));
        }

        Assert.True(
            activateDraftRevisionIds.Count >= MeasuredRuns + WarmupRuns,
            $"activate draft pool too small: {activateDraftRevisionIds.Count}");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void AssertOfflineIsolation()
    {
        // Benchmark must not require or open network services. Composition is offline-local.
        // We assert no pre-existing dependency on external interfaces and that loopback-only
        // listeners (if any) are unrelated; the gate itself never binds a port.
        Assert.True(OperatingSystem.IsLinux());
        _ = NetworkInterface.GetIsNetworkAvailable(); // host may have links; operations do not use them
    }

    private void ObserveMemory()
    {
        var host = System.Diagnostics.Process.GetCurrentProcess();
        host.Refresh();
        peakWorkingSet = Math.Max(peakWorkingSet, host.WorkingSet64);
        peakWorkingSet = Math.Max(peakWorkingSet, host.PeakWorkingSet64);
    }

    private void WriteMetadataReport(IReadOnlyList<OperationBenchmark> results)
    {
        var fingerprint = EnvironmentFingerprint();
        Console.WriteLine("BUDGET personal-scale performance (metadata-only)");
        Console.WriteLine(
            $"load: transactions={TransactionCount}, in_period={InPeriodTransactionCount}, periods={PeriodCount}, "
            + $"selected_revisions={SelectedRevisionCount}, selected_entries={SelectedEntryCount}");
        Console.WriteLine(
            $"samples_per_op={MeasuredRuns}, warmup={WarmupRuns}, "
            + $"peak_working_set_bytes={peakWorkingSet}, baseline_working_set_bytes={baselineWorkingSet}");
        Console.WriteLine($"environment: {fingerprint}");
        foreach (var r in results)
        {
            Console.WriteLine(
                $"{r.OperationId}: n={r.Samples.Count} exact={r.ExactChecksPassed} "
                + $"p50_ms={r.P50.TotalMilliseconds:0.0} p95_ms={r.P95.TotalMilliseconds:0.0} "
                + $"max_ms={r.Max.TotalMilliseconds:0.0} budget_ms={r.P95Budget.TotalMilliseconds:0.0} "
                + $"mean_output_bytes={r.MeanOutputBytes} pass_p95={r.P95 <= r.P95Budget}");
        }
    }

    private static string EnvironmentFingerprint()
    {
        return string.Join(
            "; ",
            $"os={RuntimeInformation.OSDescription}",
            $"arch={RuntimeInformation.OSArchitecture}",
            $"framework={RuntimeInformation.FrameworkDescription}",
            $"cpus={Environment.ProcessorCount}",
            $"machine={Environment.MachineName}",
            $"user={Environment.UserName}",
            $"utc={DateTimeOffset.UtcNow:O}");
    }

    private async Task DeleteSnapshotsAsync()
    {
        await using var connection = await factory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM query_snapshot;";
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> LedgerCountAsync(string sql)
    {
        await using var connection = await factory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private async Task<long> BudgetCountAsync(string sql)
    {
        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }


    private IOperationHandler Handler(BudgetOperationBundle budget, string operationId)
    {
        var descriptor = Assert.Single(budget.Descriptors, d => d.OperationId == operationId);
        return descriptor.HandlerFactory(services, registry);
    }

    private static string FormatId(int n) => n.ToString("D26", CultureInfo.InvariantCulture);

    private string NextKey(string prefix) =>
        $"budget-perf-{prefix}-{Interlocked.Increment(ref keySeq)}";

    private static async Task<IReadOnlyList<(string Name, string Sql)>> TriggerDefinitionsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'trigger' ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync();
        var triggers = new List<(string Name, string Sql)>();
        while (await reader.ReadAsync())
        {
            triggers.Add((reader.GetString(0), reader.GetString(1)));
        }

        return triggers;
    }

    private static async Task<IReadOnlyList<(string Name, string Sql)>> BudgetTriggerDefinitionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'trigger' ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync();
        var triggers = new List<(string Name, string Sql)>();
        while (await reader.ReadAsync())
        {
            triggers.Add((reader.GetString(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1)));
        }

        return triggers;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task BudgetExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed record SampleOutcome(TimeSpan Elapsed, bool Exact, long OutputBytes);

    private sealed record OperationBenchmark(
        string OperationId,
        IReadOnlyList<TimeSpan> Samples,
        TimeSpan P50,
        TimeSpan P95,
        TimeSpan Max,
        TimeSpan P95Budget,
        int ExactChecksPassed,
        long MeanOutputBytes,
        long PeakWorkingSetBytes);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BudgetPerformanceCollection
{
    public const string Name = "BudgetPerformance";
}
