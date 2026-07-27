using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Bootstrap;
using Tally.Bootstrap.Features;
using Tally.Cli;
using Tally.Contracts.Budget;
using Tally.Contracts.Budget.Plans;
using Tally.Contracts.Budget.Projection;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Categories;
using Tally.Domain.Budget.Plans;
using Tally.Domain.Ledger;
using Tally.Features.Budget.Contract;
using Tally.Features.Budget.Plans.ListRevisions;
using Tally.Features.Budget.Projection;
using Tally.Infrastructure.Budget.Storage;
using Tally.Infrastructure.Budget.Storage.Idempotency;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Budget.Acceptance;

/// <summary>
/// UC-BUDGET-005 published-surface agent contract acceptance gate (VerifiedBudgetUc005).
/// Invokes TallyProcess + OperationRegistry — never private command handlers alone.
/// Proves deterministic discovery, owner/delegated parity, bounded reads, fail-closed
/// mutation authority, version/unknown guidance, replay, host/storage metadata-only
/// failures, payload isolation, offline, and no-background/no-prompt semantics
/// (DD-BUDGET-APPLICATION-ARCHITECTURE, DD-BUDGET-CLI-OPERATION-CONTRACT,
/// DD-BUDGET-IDEMPOTENT-MUTATIONS, TC-BUDGET-CONTRACT-DISCOVERY-CONTRACT,
/// TC-BUDGET-STRUCTURED-INVOCATION-CONTRACT, TC-BUDGET-SELF-CONTAINED-LOCAL-OPERATION).
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class BudgetUc005AgentContractTests : IAsyncLifetime
{
    private const string AmountCanary = "999888777";
    private const string ReasonCanary = "CANARY_UC005_REASON_PRIVATE";
    private const string KeyCanary = "CANARY_UC005_IDEM_KEY_SECRET";

    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-budget-uc005-{Guid.NewGuid():N}");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private BudgetStateStore store = null!;
    private BudgetMutationExecutor executor = null!;
    private BudgetReadCapabilityDescriptor readCapability = null!;
    private ManualTimeProvider clock = null!;
    private int keySeq;

    public async Task InitializeAsync()
    {
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        registry = OperationRegistry.Create();
        var services = LedgerServices.Create(database);
        var bootstrap = new TallyProcess(registry, services);
        var ledger = new LedgerContractClient(registry, bootstrap);
        // Mid-July 2026: July Current, August Future, June Closed.
        clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        var budget = await BudgetOperationBundle.CreateServicesAsync(root, ledger, clock, CancellationToken.None);
        store = budget.State.Store;
        executor = budget.Executor;
        readCapability = budget.ReadCapability;
        process = new TallyProcess(registry, services with { Budget = budget.Operations });
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    // ── Discovery ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Discovery_schema_list_exposes_all_six_budget_operations_with_full_semantics()
    {
        var result = await process.RunAsync(["schema", "list"], null, CancellationToken.None);
        AssertSuccess(result, "system.schema.list");

        using var document = JsonDocument.Parse(result.Stdout);
        var operations = document.RootElement.GetProperty("result").GetProperty("operations")
            .EnumerateArray()
            .Where(op => op.GetProperty("operationId").GetString()!
                .StartsWith("budget.", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(6, operations.Length);
        var ids = operations.Select(op => op.GetProperty("operationId").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(BudgetOperationIds.All.Order(StringComparer.Ordinal), ids);

        foreach (var op in operations)
        {
            Assert.Equal("1.0", op.GetProperty("minimumContractVersion").GetString());
            Assert.Equal("1.0", op.GetProperty("maximumContractVersion").GetString());
            Assert.False(string.IsNullOrWhiteSpace(op.GetProperty("requestSchema").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(op.GetProperty("resultSchema").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(op.GetProperty("example").GetString()));
            Assert.Equal(0, op.GetProperty("successExit").GetInt32());
            Assert.Contains(
                op.GetProperty("errors").EnumerateArray(),
                e => e.GetProperty("code").GetString() == "contract.incompatible"
                     && e.GetProperty("exitCode").GetInt32() == 7);

            var kind = op.GetProperty("kind").GetString();
            var requiresKey = op.GetProperty("requiresIdempotencyKey").GetBoolean();
            var operationId = op.GetProperty("operationId").GetString()!;
            if (operationId is BudgetOperationIds.DraftCreate or BudgetOperationIds.RevisionActivate)
            {
                Assert.Equal("command", kind);
                Assert.True(requiresKey);
            }
            else
            {
                Assert.Equal("query", kind);
                Assert.False(requiresKey);
            }
        }

        // Discovery must not leak owner financial data or store paths.
        Assert.DoesNotContain("BudgetStateStore", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("budget.db", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AmountCanary, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT ", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Discovery_schema_show_is_metadata_only_without_owner_data_reads()
    {
        // Empty store: discovery must succeed without plan/revision rows.
        Assert.Equal(0L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));

        foreach (var operationId in BudgetOperationIds.All)
        {
            var result = await process.RunAsync(
                ["schema", "show", operationId],
                null,
                CancellationToken.None);
            AssertSuccess(result, "system.schema.show");
            using var document = JsonDocument.Parse(result.Stdout);
            var operation = document.RootElement.GetProperty("result").GetProperty("operation");
            Assert.Equal(operationId, operation.GetProperty("operationId").GetString());
            Assert.StartsWith("tally budget ", operation.GetProperty("cliPath").GetString(), StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(operation.GetProperty("requestSchema").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(operation.GetProperty("resultSchema").GetString()));
            Assert.Contains(
                operation.GetProperty("errors").EnumerateArray(),
                e => e.GetProperty("code").GetString() is not null
                     && e.GetProperty("exitCode").GetInt32() is >= 2 and <= 10);
            Assert.DoesNotContain("BudgetStateStore", result.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("plannedMinorUnits\":", result.Stdout, StringComparison.Ordinal);
        }

        Assert.Equal(0L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
    }

    [Fact]
    public async Task Discovery_schema_list_is_byte_stable_across_invocations()
    {
        var first = await process.RunAsync(["schema", "list"], null, CancellationToken.None);
        var second = await process.RunAsync(["schema", "list"], null, CancellationToken.None);
        AssertSuccess(first, "system.schema.list");
        AssertSuccess(second, "system.schema.list");
        Assert.Equal(first.Stdout, second.Stdout);
        Assert.Contains(BudgetOperationIds.DraftCreate, first.Stdout, StringComparison.Ordinal);
        Assert.Contains(BudgetOperationIds.InsightsEvidenceGet, first.Stdout, StringComparison.Ordinal);
    }

    // ── Owner / delegated ────────────────────────────────────────────────────

    [Fact]
    public async Task Owner_human_and_delegated_automation_produce_same_domain_invariants()
    {
        var cat = await CreateCategoryAsync("Uc005ParityCat");

        var owner = await DraftCreateAsync(
            PeriodJson(2026, 7),
            EntriesJson((cat, 12_500)),
            reason: "owner-plan",
            key: NextKey(),
            actorKind: "human",
            actorLabel: "budget-owner",
            actorRunId: null);
        var delegated = await DraftCreateAsync(
            PeriodJson(2026, 8),
            EntriesJson((cat, 12_500)),
            reason: "agent-plan",
            key: NextKey(),
            actorKind: "automation",
            actorLabel: "budget-agent-host",
            actorRunId: "agent-run-01");

        AssertSuccess(owner, BudgetOperationIds.DraftCreate);
        AssertSuccess(delegated, BudgetOperationIds.DraftCreate);

        using var ownerDoc = JsonDocument.Parse(owner.Stdout);
        using var agentDoc = JsonDocument.Parse(delegated.Stdout);
        var ownerRev = ownerDoc.RootElement.GetProperty("result").GetProperty("revision");
        var agentRev = agentDoc.RootElement.GetProperty("result").GetProperty("revision");

        Assert.Equal("draft", ownerRev.GetProperty("status").GetString());
        Assert.Equal("draft", agentRev.GetProperty("status").GetString());
        Assert.Equal(12_500, ownerRev.GetProperty("plannedTotalMinorUnits").GetInt64());
        Assert.Equal(12_500, agentRev.GetProperty("plannedTotalMinorUnits").GetInt64());
        Assert.Equal(1, ownerRev.GetProperty("entries").GetArrayLength());
        Assert.Equal(1, agentRev.GetProperty("entries").GetArrayLength());
        Assert.Equal("human", ownerRev.GetProperty("actorKind").GetString());
        Assert.Equal("automation", agentRev.GetProperty("actorKind").GetString());
        Assert.Equal("current", ownerRev.GetProperty("period").GetProperty("state").GetString());
        Assert.Equal("future", agentRev.GetProperty("period").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Owner_write_delegated_read_returns_same_revision_payload()
    {
        var cat = await CreateCategoryAsync("Uc005ReadParity");
        var draft = await DraftCreateAsync(
            PeriodJson(2026, 7),
            EntriesJson((cat, 4_200)),
            reason: "owner-write",
            key: NextKey(),
            actorKind: "human",
            actorLabel: "budget-owner",
            actorRunId: null);
        AssertSuccess(draft, BudgetOperationIds.DraftCreate);
        using var created = JsonDocument.Parse(draft.Stdout);
        var revisionId = created.RootElement.GetProperty("result").GetProperty("revision")
            .GetProperty("revisionId").GetString()!;
        var payloadHash = created.RootElement.GetProperty("result").GetProperty("revision")
            .GetProperty("payloadHash").GetString()!;

        var ownerGet = await RevisionGetAsync(revisionId, actorKind: "human", actorLabel: "budget-owner");
        var agentGet = await RevisionGetAsync(
            revisionId,
            actorKind: "automation",
            actorLabel: "budget-agent-host",
            actorRunId: "agent-run-02");

        AssertSuccess(ownerGet, BudgetOperationIds.RevisionGet);
        AssertSuccess(agentGet, BudgetOperationIds.RevisionGet);
        using var o = JsonDocument.Parse(ownerGet.Stdout);
        using var a = JsonDocument.Parse(agentGet.Stdout);
        Assert.Equal(
            o.RootElement.GetProperty("result").GetProperty("revisionId").GetString(),
            a.RootElement.GetProperty("result").GetProperty("revisionId").GetString());
        Assert.Equal(
            o.RootElement.GetProperty("result").GetProperty("payloadHash").GetString(),
            a.RootElement.GetProperty("result").GetProperty("payloadHash").GetString());
        Assert.Equal(payloadHash, a.RootElement.GetProperty("result").GetProperty("payloadHash").GetString());
        Assert.Equal(4_200, a.RootElement.GetProperty("result").GetProperty("plannedTotalMinorUnits").GetInt64());
        Assert.Equal(1, a.RootElement.GetProperty("result").GetProperty("entries").GetArrayLength());
    }

    // ── Read ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Read_revision_get_returns_only_requested_revision_data()
    {
        var cat = await CreateCategoryAsync("Uc005ReadOnly");
        var first = await DraftCreateAsync(
            PeriodJson(2026, 7), EntriesJson((cat, 100)), "first", NextKey());
        var second = await DraftCreateAsync(
            PeriodJson(2026, 7), EntriesJson((cat, 200)), "second", NextKey());
        AssertSuccess(first, BudgetOperationIds.DraftCreate);
        AssertSuccess(second, BudgetOperationIds.DraftCreate);
        using var firstDoc = JsonDocument.Parse(first.Stdout);
        using var secondDoc = JsonDocument.Parse(second.Stdout);
        var firstId = firstDoc.RootElement.GetProperty("result").GetProperty("revision")
            .GetProperty("revisionId").GetString()!;
        var secondId = secondDoc.RootElement.GetProperty("result").GetProperty("revision")
            .GetProperty("revisionId").GetString()!;

        var get = await RevisionGetAsync(firstId);
        AssertSuccess(get, BudgetOperationIds.RevisionGet);
        using var doc = JsonDocument.Parse(get.Stdout);
        var revision = doc.RootElement.GetProperty("result");
        Assert.Equal(firstId, revision.GetProperty("revisionId").GetString());
        Assert.Equal(100, revision.GetProperty("plannedTotalMinorUnits").GetInt64());
        Assert.DoesNotContain(secondId, get.Stdout, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Undefined, revision.TryGetProperty("recommendations", out _)
            ? revision.GetProperty("recommendations").ValueKind
            : JsonValueKind.Undefined);
        Assert.DoesNotContain("pace", get.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("forecast", get.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Read_position_and_insights_return_structured_requested_data_only()
    {
        var cat = await CreateCategoryAsync("Uc005PositionRead");
        var draft = await DraftCreateAsync(
            PeriodJson(2026, 7), EntriesJson((cat, 5_000)), "pos", NextKey());
        AssertSuccess(draft, BudgetOperationIds.DraftCreate);
        using var draftDoc = JsonDocument.Parse(draft.Stdout);
        var revisionId = draftDoc.RootElement.GetProperty("result").GetProperty("revision")
            .GetProperty("revisionId").GetString()!;

        var activate = await ActivateAsync(revisionId, "go-live", NextKey());
        AssertSuccess(activate, BudgetOperationIds.RevisionActivate);

        var position = await process.RunAsync(
            ["budget", "position", "get", "--input", "-"],
            Envelope(
                """{"contractVersion":"1.0","period":{"year":2026,"month":7,"currencyCode":"ZAR"}}""",
                idempotencyKey: null),
            CancellationToken.None);
        AssertSuccess(position, BudgetOperationIds.PositionGet);
        using var posDoc = JsonDocument.Parse(position.Stdout);
        Assert.Equal(revisionId, posDoc.RootElement.GetProperty("result").GetProperty("position")
            .GetProperty("revisionId").GetString());
        Assert.DoesNotContain("recommendation", position.Stdout, StringComparison.OrdinalIgnoreCase);

        var evidence = await process.RunAsync(
            ["budget", "insights", "evidence", "get", "--input", "-"],
            Envelope(
                """{"contractVersion":"1.0","budgetPeriod":{"year":2026,"month":7,"currencyCode":"ZAR"}}""",
                idempotencyKey: null),
            CancellationToken.None);
        AssertSuccess(evidence, BudgetOperationIds.InsightsEvidenceGet);
        using var evDoc = JsonDocument.Parse(evidence.Stdout);
        Assert.Equal(
            "bound_revision",
            evDoc.RootElement.GetProperty("result").GetProperty("evidence").GetProperty("planState").GetString());
        Assert.DoesNotContain("forecast", evidence.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("narrative", evidence.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    // ── Mutation ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mutation_draft_create_requires_actor_reason_and_idempotency_key()
    {
        var before = await BudgetMutationSnapshotAsync();

        var noKey = await process.RunAsync(
            ["budget", "plan", "draft", "create", "--input", "-"],
            """{"contractVersion":"1.0","actor":{"kind":"automation","label":"budget-uc005","runId":"run-01"},"input":{"contractVersion":"1.0","period":{"year":2026,"month":7,"currencyCode":"ZAR"},"entries":[],"reason":"missing-key"}}""",
            CancellationToken.None);
        AssertError(noKey, 3, "validation.invalid_input");

        var noActor = await process.RunAsync(
            ["budget", "plan", "draft", "create", "--input", "-"],
            """{"contractVersion":"1.0","idempotencyKey":"k-no-actor","input":{"contractVersion":"1.0","period":{"year":2026,"month":7,"currencyCode":"ZAR"},"entries":[],"reason":"missing-actor"}}""",
            CancellationToken.None);
        AssertError(noActor, 3, "validation.invalid_input");

        var blankReason = await DraftCreateAsync(
            PeriodJson(2026, 7), "[]", reason: "   ", key: NextKey());
        AssertDomainError(blankReason, 3, BudgetErrors.InvalidInput);

        Assert.Equal(before, await BudgetMutationSnapshotAsync());
    }

    [Fact]
    public async Task Mutation_activate_requires_explicit_intent_authority_and_key()
    {
        var cat = await CreateCategoryAsync("Uc005ActAuth");
        var draft = await DraftCreateAsync(
            PeriodJson(2026, 7), EntriesJson((cat, 10)), "draft", NextKey());
        AssertSuccess(draft, BudgetOperationIds.DraftCreate);
        using var doc = JsonDocument.Parse(draft.Stdout);
        var revisionId = doc.RootElement.GetProperty("result").GetProperty("revision")
            .GetProperty("revisionId").GetString()!;
        var snapshot = await BudgetMutationSnapshotAsync();

        var noKeyBody =
            "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"budget-uc005\",\"runId\":\"run-01\"},\"input\":{\"contractVersion\":\"1.0\",\"revisionId\":\""
            + revisionId
            + "\",\"reason\":\"go\"}}";
        var noKey = await process.RunAsync(
            ["budget", "plan", "revision", "activate", "--input", "-"],
            noKeyBody,
            CancellationToken.None);
        AssertError(noKey, 3, "validation.invalid_input");

        var noActorBody =
            "{\"contractVersion\":\"1.0\",\"idempotencyKey\":\"k-act\",\"input\":{\"contractVersion\":\"1.0\",\"revisionId\":\""
            + revisionId
            + "\",\"reason\":\"go\"}}";
        var noActor = await process.RunAsync(
            ["budget", "plan", "revision", "activate", "--input", "-"],
            noActorBody,
            CancellationToken.None);
        AssertError(noActor, 3, "validation.invalid_input");

        var blankReason = await ActivateAsync(revisionId, "   ", NextKey());
        AssertDomainError(blankReason, 3, BudgetErrors.InvalidInput);

        Assert.Equal(snapshot, await BudgetMutationSnapshotAsync());
        Assert.Equal(0L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
    }

    // ── Version / unknown ────────────────────────────────────────────────────

    [Fact]
    public async Task Version_unsupported_contract_returns_compatibility_guidance()
    {
        var cat = await CreateCategoryAsync("Uc005BadVer");
        var body = Envelope(
            $$"""{"contractVersion":"9.9","period":{"year":2026,"month":7,"currencyCode":"ZAR"},"entries":[{"categoryId":"{{cat}}","plannedMinorUnits":1}],"reason":"bad-version"}""",
            NextKey());
        var result = await process.RunAsync(
            ["budget", "plan", "draft", "create", "--input", "-"],
            body,
            CancellationToken.None);

        Assert.Equal(7, result.ExitCode);
        AssertDomainError(result, 7, BudgetErrors.UnsupportedVersion);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("compatibility", document.RootElement.GetProperty("error").GetProperty("category").GetString());
        Assert.Equal(0L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
        Assert.DoesNotContain(cat, result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_operation_provides_stable_discoverable_guidance()
    {
        var unknownCli = await process.RunAsync(
            ["budget", "plan", "delete", "--input", "-"],
            Envelope("""{"contractVersion":"1.0"}""", idempotencyKey: null),
            CancellationToken.None);
        Assert.Equal(2, unknownCli.ExitCode);
        AssertError(unknownCli, 2, "operation.unknown");
        using var cliDoc = JsonDocument.Parse(unknownCli.Stdout);
        Assert.Equal("usage", cliDoc.RootElement.GetProperty("error").GetProperty("category").GetString());

        var unknownShow = await process.RunAsync(
            ["schema", "show", "budget.plan.delete"],
            null,
            CancellationToken.None);
        Assert.Equal(4, unknownShow.ExitCode);
        AssertError(unknownShow, 4, "operation.not_found");
        using var showDoc = JsonDocument.Parse(unknownShow.Stdout);
        Assert.Equal("not_found", showDoc.RootElement.GetProperty("error").GetProperty("category").GetString());

        // Compatibility path remains discoverable via schema inventory.
        var list = await process.RunAsync(["schema", "list"], null, CancellationToken.None);
        AssertSuccess(list, "system.schema.list");
        Assert.Contains(BudgetOperationIds.DraftCreate, list.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("budget.plan.delete", list.Stdout, StringComparison.Ordinal);
    }

    // ── Authority ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Authority_insights_capability_excludes_mutations_and_cannot_grant_write()
    {
        Assert.Equal(3, readCapability.AllowedOperations.Count);
        Assert.Equal(
            BudgetReadCapabilityOperations.All,
            readCapability.AllowedOperations.Select(o => o.OperationId));
        Assert.All(readCapability.AllowedOperations, op =>
        {
            Assert.Equal("query", op.Kind);
            Assert.False(op.RequiresIdempotencyKey);
        });
        Assert.DoesNotContain(
            BudgetOperationIds.DraftCreate,
            readCapability.AllowedOperations.Select(o => o.OperationId));
        Assert.DoesNotContain(
            BudgetOperationIds.RevisionActivate,
            readCapability.AllowedOperations.Select(o => o.OperationId));
        Assert.True(BudgetReadProjectionModule.IsAllowedReadOperation(BudgetOperationIds.PositionGet));
        Assert.True(BudgetReadProjectionModule.IsAllowedReadOperation(BudgetOperationIds.InsightsEvidenceGet));
        Assert.False(BudgetReadProjectionModule.IsAllowedReadOperation(BudgetOperationIds.DraftCreate));
        Assert.False(BudgetReadProjectionModule.IsAllowedReadOperation(BudgetOperationIds.RevisionActivate));

        // Insights evidence is query-only at the process surface (no idempotency accepted).
        var withKey = await process.RunAsync(
            ["budget", "insights", "evidence", "get", "--input", "-"],
            Envelope(
                """{"contractVersion":"1.0","budgetPeriod":{"year":2026,"month":7,"currencyCode":"ZAR"}}""",
                "must-not-be-accepted"),
            CancellationToken.None);
        AssertError(withKey, 3, "validation.invalid_input");
        Assert.Equal(0L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_idempotency_record;"));
    }

    [Fact]
    public async Task Authority_unprovable_integrity_evidence_fails_closed_metadata_only()
    {
        var catA = await CreateCategoryAsync("Uc005OverflowA");
        var catB = await CreateCategoryAsync("Uc005OverflowB");
        var planId = LedgerId.New().ToString();
        var revisionId = LedgerId.New().ToString();
        await SeedOverflowDraftAsync(planId, revisionId, catA, catB);

        var result = await RevisionGetAsync(revisionId);
        Assert.Equal(8, result.ExitCode);
        AssertDomainError(result, 8, BudgetErrors.Integrity);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("integrity", document.RootElement.GetProperty("error").GetProperty("category").GetString());
        Assert.False(
            document.RootElement.TryGetProperty("result", out var resultEl)
            && resultEl.ValueKind is not JsonValueKind.Null
            && resultEl.ValueKind is not JsonValueKind.Undefined);
        Assert.DoesNotContain("plannedMinorUnits", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(long.MaxValue.ToString(CultureInfo.InvariantCulture), result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(planId, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(AmountCanary, result.Stderr, StringComparison.Ordinal);
    }

    // ── Replay ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Replay_equivalent_retry_returns_same_stable_result()
    {
        var cat = await CreateCategoryAsync("Uc005Replay");
        var key = NextKey();
        var first = await DraftCreateAsync(
            PeriodJson(2026, 7), EntriesJson((cat, 42)), "replay", key);
        var second = await DraftCreateAsync(
            PeriodJson(2026, 7), EntriesJson((cat, 42)), "replay", key);

        AssertSuccess(first, BudgetOperationIds.DraftCreate);
        AssertSuccess(second, BudgetOperationIds.DraftCreate);
        using var d1 = JsonDocument.Parse(first.Stdout);
        using var d2 = JsonDocument.Parse(second.Stdout);
        var r1 = d1.RootElement.GetProperty("result").GetProperty("revision");
        var r2 = d2.RootElement.GetProperty("result").GetProperty("revision");
        Assert.Equal(r1.GetProperty("revisionId").GetString(), r2.GetProperty("revisionId").GetString());
        Assert.Equal(r1.GetProperty("planId").GetString(), r2.GetProperty("planId").GetString());
        Assert.Equal(r1.GetProperty("payloadHash").GetString(), r2.GetProperty("payloadHash").GetString());
        Assert.Equal(r1.GetProperty("revisionNumber").GetInt32(), r2.GetProperty("revisionNumber").GetInt32());
        Assert.Equal(1L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
        Assert.Equal(1L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_idempotency_record;"));
        Assert.Equal(1L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;"));
    }

    [Fact]
    public async Task Replay_conflicting_retry_performs_no_state_change()
    {
        var cat = await CreateCategoryAsync("Uc005Conflict");
        var key = NextKey();
        var first = await DraftCreateAsync(
            PeriodJson(2026, 7), EntriesJson((cat, 10)), "a", key);
        AssertSuccess(first, BudgetOperationIds.DraftCreate);
        var snapshot = await BudgetMutationSnapshotAsync();

        var conflict = await DraftCreateAsync(
            PeriodJson(2026, 7), EntriesJson((cat, 99)), "b", key);
        AssertDomainError(conflict, 5, BudgetErrors.IdempotencyConflict);
        Assert.Equal(snapshot, await BudgetMutationSnapshotAsync());
        Assert.Equal(1L, await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_revision;"));
        using var doc = JsonDocument.Parse(first.Stdout);
        var revisionId = doc.RootElement.GetProperty("result").GetProperty("revision")
            .GetProperty("revisionId").GetString()!;
        Assert.Equal(10L, await BudgetScalarAsync(
            $"SELECT planned_minor_units FROM budget_plan_entry WHERE revision_id = '{revisionId}';"));
        Assert.DoesNotContain(key, conflict.Stderr, StringComparison.Ordinal);
    }

    // ── Host / storage ───────────────────────────────────────────────────────

    [Fact]
    public async Task Host_pre_commit_fault_returns_structured_metadata_only_diagnostics()
    {
        var cat = await CreateCategoryAsync("Uc005HostFault");
        var draft = await DraftCreateAsync(
            PeriodJson(2026, 7), EntriesJson((cat, 11)), "host", NextKey());
        AssertSuccess(draft, BudgetOperationIds.DraftCreate);
        using var doc = JsonDocument.Parse(draft.Stdout);
        var revisionId = doc.RootElement.GetProperty("result").GetProperty("revision")
            .GetProperty("revisionId").GetString()!;
        var key = NextKey();
        executor.FaultPoint = BudgetMutationFaultPoint.BeforeCommit;

        var interrupted = await ActivateAsync(revisionId, "cut", key);
        executor.FaultPoint = BudgetMutationFaultPoint.None;

        Assert.Equal(10, interrupted.ExitCode);
        Assert.Contains("host.unexpected", interrupted.Stdout, StringComparison.Ordinal);
        using var err = JsonDocument.Parse(interrupted.Stdout);
        Assert.Equal("error", err.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("host", err.RootElement.GetProperty("error").GetProperty("category").GetString());
        Assert.Equal("tally: host.unexpected", interrupted.Stderr);
        Assert.DoesNotContain("11", interrupted.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(revisionId, interrupted.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(AmountCanary, interrupted.Stdout, StringComparison.Ordinal);
        Assert.Equal(0L, await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan_revision WHERE status = 'Active';"));
        Assert.Equal("Draft", await BudgetTextAsync(
            $"SELECT status FROM budget_plan_revision WHERE revision_id = '{revisionId}';"));
    }

    [Fact]
    public async Task Host_resource_limit_list_is_structured_metadata_only()
    {
        var body = Envelope(
            $$"""{"contractVersion":"1.0","period":{"year":2026,"month":7,"currencyCode":"ZAR"},"limit":{{ListBudgetPlanRevisionsQuery.MaxLimit + 1}}}""",
            idempotencyKey: null);
        var result = await process.RunAsync(
            ["budget", "plan", "revision", "list", "--input", "-"],
            body,
            CancellationToken.None);

        Assert.Equal(9, result.ExitCode);
        AssertDomainError(result, 9, BudgetErrors.ResourceLimit);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("host", document.RootElement.GetProperty("error").GetProperty("category").GetString());
        Assert.Equal("tally: " + BudgetErrors.ResourceLimit, result.Stderr);
        Assert.DoesNotContain("JsonException", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("stack", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plannedMinorUnits", result.Stdout, StringComparison.Ordinal);
    }

    // ── Payload isolation ────────────────────────────────────────────────────

    [Fact]
    public async Task Payload_isolation_financial_json_uses_stdin_never_argv_or_stderr()
    {
        var cat = await CreateCategoryAsync("Uc005PayloadIso");
        var args = new[] { "budget", "plan", "draft", "create", "--input", "-" };
        Assert.DoesNotContain(args, a => a.Contains(AmountCanary, StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.Contains(ReasonCanary, StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.Contains('{'));

        var inputJson =
            "{\"contractVersion\":\"1.0\",\"period\":{\"year\":2026,\"month\":7,\"currencyCode\":\"ZAR\"},"
            + "\"entries\":[{\"categoryId\":\"" + cat + "\",\"plannedMinorUnits\":" + AmountCanary + "}],"
            + "\"reason\":\"" + ReasonCanary + "\"}";
        var body = Envelope(inputJson, KeyCanary);
        var success = await process.RunAsync(args, body, CancellationToken.None);
        AssertSuccess(success, BudgetOperationIds.DraftCreate);
        Assert.True(string.IsNullOrEmpty(success.Stderr));
        Assert.DoesNotContain(AmountCanary, success.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(ReasonCanary, success.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(KeyCanary, success.Stderr, StringComparison.Ordinal);

        var conflictInput =
            "{\"contractVersion\":\"1.0\",\"period\":{\"year\":2026,\"month\":7,\"currencyCode\":\"ZAR\"},"
            + "\"entries\":[{\"categoryId\":\"" + cat + "\",\"plannedMinorUnits\":1}],"
            + "\"reason\":\"" + ReasonCanary + "\"}";
        var conflict = await process.RunAsync(args, Envelope(conflictInput, KeyCanary), CancellationToken.None);
        AssertDomainError(conflict, 5, BudgetErrors.IdempotencyConflict);
        Assert.DoesNotContain(AmountCanary, conflict.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(ReasonCanary, conflict.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(KeyCanary, conflict.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(KeyCanary, conflict.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(ReasonCanary, conflict.Stdout, StringComparison.Ordinal);

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".db", StringComparison.Ordinal)
                || file.EndsWith("-wal", StringComparison.Ordinal)
                || file.EndsWith("-shm", StringComparison.Ordinal)
                || file.EndsWith("CURRENT", StringComparison.Ordinal))
            {
                continue;
            }

            var name = Path.GetFileName(file);
            Assert.DoesNotContain(AmountCanary, name, StringComparison.Ordinal);
            Assert.DoesNotContain(ReasonCanary, name, StringComparison.Ordinal);
            Assert.DoesNotContain(KeyCanary, name, StringComparison.Ordinal);
        }
    }

    // ── Offline / no-background ──────────────────────────────────────────────

    [Fact]
    public void Offline_budget_composition_has_no_network_or_plugin_surface()
    {
        var repositoryRoot = RepositoryRoot();
        string[] paths =
        [
            Path.Combine(repositoryRoot, "src", "Tally", "Bootstrap", "Features", "BudgetExtensions.cs"),
            Path.Combine(repositoryRoot, "src", "Tally", "Bootstrap", "Features", "BudgetStateExtensions.cs"),
            Path.Combine(repositoryRoot, "src", "Tally", "Infrastructure", "Budget"),
            Path.Combine(repositoryRoot, "src", "Tally", "Features", "Budget")
        ];

        var composition = string.Join(
            '\n',
            paths
                .SelectMany(path => Directory.Exists(path)
                    ? Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)
                    : File.Exists(path) ? [path] : Array.Empty<string>())
                .Order(StringComparer.Ordinal)
                .Select(File.ReadAllText));

        string[] forbidden =
        [
            "FastEndpoints", "Aspire", "Npgsql", "EntityFramework", "Microsoft.AspNetCore",
            "HttpListener", "TcpListener", "WebApplication", "UseKestrel", "AddPlugins", "MEF",
            "Assembly.LoadFrom", "Assembly.Load(", "Process.Start", "HttpClient",
            "using MailKit", "using MimeKit", "WebSocket"
        ];
        Assert.All(forbidden, token =>
            Assert.DoesNotContain(token, composition, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void No_background_registry_has_no_daemon_prompt_or_remote_budget_operations()
    {
        var ids = OperationRegistry.Create().Descriptors
            .Select(d => d.OperationId)
            .Where(id => id.StartsWith("budget.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(6, ids.Count);
        foreach (var forbidden in new[]
                 {
                     "budget.sync", "budget.import", "budget.export", "budget.watch", "budget.schedule",
                     "budget.daemon", "budget.service", "budget.webhook", "budget.push", "budget.pull",
                     "budget.prompt", "budget.interactive", "budget.invoke", "budget.run", "budget.save"
                 })
        {
            Assert.DoesNotContain(forbidden, ids);
        }

        foreach (var operationId in BudgetOperationIds.All)
        {
            var descriptor = OperationRegistry.Create().Find(operationId)!;
            Assert.DoesNotContain("prompt", descriptor.CliPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("interactive", descriptor.Example, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("--input", descriptor.Example, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task No_background_invocation_is_non_interactive_and_single_envelope()
    {
        var cat = await CreateCategoryAsync("Uc005NoPrompt");
        var result = await DraftCreateAsync(
            PeriodJson(2026, 7), EntriesJson((cat, 7)), "one-shot", NextKey());

        AssertSuccess(result, BudgetOperationIds.DraftCreate);
        Assert.True(string.IsNullOrEmpty(result.Stderr));
        Assert.Single(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("success", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("1.0", document.RootElement.GetProperty("contractVersion").GetString());
        // No multi-turn / follow-up prompt fields on the business envelope.
        Assert.False(document.RootElement.TryGetProperty("prompt", out _));
        Assert.False(document.RootElement.TryGetProperty("continue", out _));
        Assert.False(document.RootElement.TryGetProperty("backgroundJobId", out _));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<ProcessResult> DraftCreateAsync(
        string periodJson,
        string entriesJson,
        string reason,
        string key,
        string actorKind = "automation",
        string actorLabel = "budget-uc005",
        string? actorRunId = "run-01")
    {
        var input =
            "{\"contractVersion\":\"1.0\",\"period\":" + periodJson
            + ",\"entries\":" + entriesJson
            + ",\"reason\":" + JsonSerializer.Serialize(reason) + "}";
        return await process.RunAsync(
            ["budget", "plan", "draft", "create", "--input", "-"],
            Envelope(input, key, actorKind, actorLabel, actorRunId),
            CancellationToken.None);
    }

    private async Task<ProcessResult> ActivateAsync(string revisionId, string reason, string key)
    {
        var input =
            "{\"contractVersion\":\"1.0\",\"revisionId\":\"" + revisionId
            + "\",\"reason\":" + JsonSerializer.Serialize(reason) + "}";
        return await process.RunAsync(
            ["budget", "plan", "revision", "activate", "--input", "-"],
            Envelope(input, key),
            CancellationToken.None);
    }

    private async Task<ProcessResult> RevisionGetAsync(
        string revisionId,
        string actorKind = "automation",
        string actorLabel = "budget-uc005",
        string? actorRunId = "run-01")
    {
        var input = "{\"contractVersion\":\"1.0\",\"revisionId\":\"" + revisionId + "\"}";
        return await process.RunAsync(
            ["budget", "plan", "revision", "get", "--input", "-"],
            Envelope(input, idempotencyKey: null, actorKind, actorLabel, actorRunId),
            CancellationToken.None);
    }

    private static string PeriodJson(int year, int month) =>
        $$"""{"year":{{year}},"month":{{month}},"currencyCode":"ZAR"}""";

    private static string EntriesJson(params (string CategoryId, long Amount)[] entries) =>
        "[" + string.Join(
            ",",
            entries.Select(e =>
                "{\"categoryId\":\"" + e.CategoryId
                + "\",\"plannedMinorUnits\":" + e.Amount.ToString(CultureInfo.InvariantCulture) + "}"))
        + "]";

    private string NextKey() =>
        "uc005-key-" + (++keySeq).ToString("D4", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];

    private static string Envelope(
        string inputJson,
        string? idempotencyKey,
        string actorKind = "automation",
        string actorLabel = "budget-uc005",
        string? actorRunId = "run-01")
    {
        var actor = actorRunId is null
            ? "{\"kind\":\"" + actorKind + "\",\"label\":\"" + actorLabel + "\"}"
            : "{\"kind\":\"" + actorKind + "\",\"label\":\"" + actorLabel + "\",\"runId\":\"" + actorRunId + "\"}";
        return idempotencyKey is null
            ? "{\"contractVersion\":\"1.0\",\"actor\":" + actor + ",\"input\":" + inputJson + "}"
            : "{\"contractVersion\":\"1.0\",\"actor\":" + actor
              + ",\"idempotencyKey\":" + JsonSerializer.Serialize(idempotencyKey)
              + ",\"input\":" + inputJson + "}";
    }

    private async Task<string> CreateCategoryAsync(string name)
    {
        var request =
            "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"budget-uc005\",\"runId\":\"run-01\"},\"idempotencyKey\":\""
            + NextKey()
            + "\",\"input\":{\"name\":\"" + name + "\"}}";
        var result = await process.RunAsync(
            ["ledger", "category", "create", "--input", "-"],
            request,
            CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        return document.RootElement.GetProperty("result").GetProperty("categoryId").GetString()!;
    }

    private async Task SeedOverflowDraftAsync(
        string planId,
        string revisionId,
        string categoryA,
        string categoryB)
    {
        var createdAt = BudgetPlanRevision.FormatUtc(clock.GetUtcNow());
        var entryRows = new[]
        {
            new BudgetPlanEntryRow(revisionId, categoryA, long.MaxValue),
            new BudgetPlanEntryRow(revisionId, categoryB, 1)
        };
        var domainEntries = entryRows
            .Select(e => new BudgetPlanEntry(e.CategoryId, e.PlannedMinorUnits))
            .ToArray();
        var payloadHash = BudgetPlanRevision.ComputePayloadHash(CategoryContractVersions.Current, domainEntries);

        await using var connection = await store.OpenMigratedAsync(CancellationToken.None);
        await using var transaction = store.BeginImmediate(connection);
        await store.InsertPlanAsync(
            connection,
            transaction,
            new BudgetPlanRow(planId, "2026-07-01", "2026-08-01", "ZAR", ActiveRevisionId: null, createdAt),
            CancellationToken.None);
        await store.InsertDraftRevisionAsync(
            connection,
            transaction,
            new BudgetPlanRevisionRow(
                revisionId,
                planId,
                1,
                BudgetRevisionStatus.Draft,
                "automation",
                "budget-uc005",
                "run-01",
                "seeded overflow draft",
                createdAt,
                CategoryContractVersions.Current,
                payloadHash,
                ActivatedAtUtc: null,
                SupersededAtUtc: null,
                SupersededByRevisionId: null),
            entryRows,
            new BudgetLifecycleEventRow(
                LedgerId.New().ToString(),
                planId,
                revisionId,
                BudgetPlanLifecycle.EventDraftCreated,
                "automation",
                "budget-uc005",
                "run-01",
                "seeded overflow draft",
                createdAt,
                PriorStatus: null,
                ResultingStatus: BudgetRowMapper.FormatStatus(BudgetRevisionStatus.Draft),
                ReplacementRevisionId: null,
                EventSequence: 1),
            CancellationToken.None);
        await transaction.CommitAsync();
    }

    private async Task<string> BudgetMutationSnapshotAsync()
    {
        if (!File.Exists(BudgetDatabasePath()))
        {
            return "absent";
        }

        var plans = await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan;");
        var revs = await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_revision;");
        var entries = await BudgetCountAsync("SELECT COUNT(*) FROM budget_plan_entry;");
        var events = await BudgetCountAsync("SELECT COUNT(*) FROM budget_lifecycle_event;");
        var idemp = await BudgetCountAsync("SELECT COUNT(*) FROM budget_idempotency_record;");
        var active = await BudgetCountAsync(
            "SELECT COUNT(*) FROM budget_plan WHERE active_revision_id IS NOT NULL;");
        return $"{plans}|{revs}|{entries}|{events}|{idemp}|{active}";
    }

    private string BudgetDatabasePath() => Path.Combine(root, "budget", "budget.db");

    private async Task<long> BudgetCountAsync(string sql)
    {
        var path = BudgetDatabasePath();
        if (!File.Exists(path))
        {
            return 0;
        }

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var scalar = await command.ExecuteScalarAsync();
        return scalar is null or DBNull ? 0L : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    private async Task<long> BudgetScalarAsync(string sql) => await BudgetCountAsync(sql);

    private async Task<string?> BudgetTextAsync(string sql)
    {
        var path = BudgetDatabasePath();
        if (!File.Exists(path))
        {
            return null;
        }

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var scalar = await command.ExecuteScalarAsync();
        return scalar is null or DBNull ? null : Convert.ToString(scalar, CultureInfo.InvariantCulture);
    }

    private static void AssertSuccess(ProcessResult result, string operationId)
    {
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Single(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(operationId, document.RootElement.GetProperty("operationId").GetString());
        Assert.Equal("success", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("1.0", document.RootElement.GetProperty("contractVersion").GetString());
    }

    private static void AssertDomainError(ProcessResult result, int exitCode, string domainCode)
    {
        Assert.Equal(exitCode, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("error", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(domainCode, document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains(domainCode, result.Stderr, StringComparison.Ordinal);
    }

    private static void AssertError(ProcessResult result, int exitCode, string code)
    {
        Assert.Equal(exitCode, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal("error", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(code, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tally.slnx"))
                || File.Exists(Path.Combine(directory.FullName, "Tally.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test base directory.");
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
