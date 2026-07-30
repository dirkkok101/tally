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
using Tally.Infrastructure.Budget.Storage;
using Tally.Infrastructure.Storage;
using Tally.Integration.Ledger;
using Xunit;

namespace Tally.Tests.Budget.Acceptance;

/// <summary>
/// TASK-BUDGET-GATE-INT-ENVELOPE-PROVENANCE / TC-BUDGET-ENVELOPE-MEMBER-PROVENANCE /
/// TC-BUDGET-ENVELOPE-REPARENT-RELENSES — published-surface envelope proofs via TallyProcess.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class BudgetEnvelopeProvenanceTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-budget-env-prov-{Guid.NewGuid():N}");
    private readonly SafeActor actor = new("automation", "budget-env-prov", "run-01");
    private OperationRegistry registry = null!;
    private TallyProcess process = null!;
    private ManualTimeProvider clock = null!;
    private string accountId = null!;
    private int keySeq;

    public async Task InitializeAsync()
    {
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        registry = OperationRegistry.Create();
        var ledgerServices = LedgerServices.Create(database);
        var bootstrap = new TallyProcess(registry, ledgerServices);
        var ledger = new LedgerContractClient(registry, bootstrap);

        clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        var budgetServices = await BudgetOperationBundle.CreateServicesAsync(root, ledger, clock, CancellationToken.None);
        process = new TallyProcess(registry, ledgerServices with { Budget = budgetServices.Operations });

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

    // ── TC-BUDGET-ENVELOPE-MEMBER-PROVENANCE ─────────────────────────────────

    [Fact]
    public async Task Insights_evidence_reports_ancestry_and_effective_category_for_depth_three_tree()
    {
        // Depth-three tree; entries on root and child; actuals on root, child, grand, and unfunded sibling.
        var rootCat = await CreateCategoryAsync("EnvRoot");
        var childCat = await CreateCategoryAsync("EnvChild", rootCat.CategoryId);
        var grandCat = await CreateCategoryAsync("EnvGrand", childCat.CategoryId);
        var unfunded = await CreateCategoryAsync("EnvUnfunded");

        var draft = await CreateDraftAsync(
            Period(2026, 7),
            [Entry(rootCat.CategoryId, 600_000), Entry(childCat.CategoryId, 100_000)],
            "envelope tree");
        var activated = await ActivateAsync(draft.RevisionId, "go-live");

        var txRoot = await RecordAsync("-1.00", "2026-07-02", "root-direct");
        await AssignCategoryAsync(txRoot.TransactionId, rootCat.CategoryId);
        var txChild = await RecordAsync("-2.00", "2026-07-03", "child-direct");
        await AssignCategoryAsync(txChild.TransactionId, childCat.CategoryId);
        var txGrand = await RecordAsync("-3.00", "2026-07-04", "grand-absorbed");
        await AssignCategoryAsync(txGrand.TransactionId, grandCat.CategoryId);
        var txUnfunded = await RecordAsync("-0.40", "2026-07-05", "unfunded");
        await AssignCategoryAsync(txUnfunded.TransactionId, unfunded.CategoryId);
        var txUncat = await RecordAsync("-0.10", "2026-07-06", "uncategorized");

        var evidence = await GetEvidenceSuccessAsync(Period(2026, 7));

        Assert.Equal(BudgetInsightPlanState.BoundRevision, evidence.PlanState);
        Assert.NotNull(evidence.Position);
        Assert.Equal(activated.RevisionId, evidence.Position!.RevisionId);
        Assert.Equal(BudgetPositionCalculator.CalculationSchemaVersion, evidence.CalculationSchemaVersion);

        // Position: root absorbs nothing from child subtree (child has its own entry);
        // child absorbs grand; unfunded is unbudgeted; uncategorized separate.
        var rootPos = evidence.Position.CategoryPositions.Single(p => p.CategoryId == rootCat.CategoryId);
        Assert.Equal(BudgetCategoryPositionKind.Budgeted, rootPos.Kind);
        Assert.Equal(100, rootPos.ActualMinorUnits);
        Assert.Equal(100, rootPos.DirectActualMinorUnits);
        Assert.Equal(0, rootPos.DescendantActualMinorUnits);

        var childPos = evidence.Position.CategoryPositions.Single(p => p.CategoryId == childCat.CategoryId);
        Assert.Equal(BudgetCategoryPositionKind.Budgeted, childPos.Kind);
        Assert.Equal(500, childPos.ActualMinorUnits); // 200 + 300
        Assert.Equal(200, childPos.DirectActualMinorUnits);
        Assert.Equal(300, childPos.DescendantActualMinorUnits);
        Assert.Equal([grandCat.CategoryId], childPos.AbsorbedCategoryIds);

        var unbudgetedPos = evidence.Position.CategoryPositions.Single(
            p => p.Kind == BudgetCategoryPositionKind.Unbudgeted);
        Assert.Equal(unfunded.CategoryId, unbudgetedPos.CategoryId);
        Assert.Equal(40, unbudgetedPos.ActualMinorUnits);
        Assert.Equal(0, unbudgetedPos.DescendantActualMinorUnits);

        Assert.Equal(10, evidence.Position.UncategorizedPosition.ActualMinorUnits);

        // Members: frozen ancestry root-first self-last + effective category.
        var byTx = evidence.ActualMembers.ToDictionary(m => m.TransactionId, StringComparer.Ordinal);

        var rootMember = byTx[txRoot.TransactionId];
        Assert.Equal([rootCat.CategoryId], rootMember.AncestryIds);
        Assert.Equal(rootCat.CategoryId, rootMember.EffectiveCategoryId);

        var childMember = byTx[txChild.TransactionId];
        Assert.Equal([rootCat.CategoryId, childCat.CategoryId], childMember.AncestryIds);
        Assert.Equal(childCat.CategoryId, childMember.EffectiveCategoryId);

        var grandMember = byTx[txGrand.TransactionId];
        Assert.Equal(
            [rootCat.CategoryId, childCat.CategoryId, grandCat.CategoryId],
            grandMember.AncestryIds);
        Assert.Equal(childCat.CategoryId, grandMember.EffectiveCategoryId);

        var unfundedMember = byTx[txUnfunded.TransactionId];
        Assert.Equal([unfunded.CategoryId], unfundedMember.AncestryIds);
        Assert.Null(unfundedMember.EffectiveCategoryId);

        var uncatMember = byTx[txUncat.TransactionId];
        Assert.Null(uncatMember.CategoryId);
        Assert.Empty(uncatMember.AncestryIds);
        Assert.Null(uncatMember.EffectiveCategoryId);
    }

    [Fact]
    public async Task Insights_unbudgeted_and_uncategorized_members_have_null_effective_category()
    {
        var funded = await CreateCategoryAsync("FundedOnly");
        var stray = await CreateCategoryAsync("StrayLeaf");
        var draft = await CreateDraftAsync(Period(2026, 7), [Entry(funded.CategoryId, 50_000)], "null-effective");
        await ActivateAsync(draft.RevisionId, "act");

        var txStray = await RecordAsync("-1.00", "2026-07-10", "stray");
        await AssignCategoryAsync(txStray.TransactionId, stray.CategoryId);
        var txUncat = await RecordAsync("-0.50", "2026-07-11", "no-cat");

        var evidence = await GetEvidenceSuccessAsync(Period(2026, 7));
        var strayMember = evidence.ActualMembers.Single(m => m.TransactionId == txStray.TransactionId);
        var uncatMember = evidence.ActualMembers.Single(m => m.TransactionId == txUncat.TransactionId);

        Assert.Equal(stray.CategoryId, strayMember.CategoryId);
        Assert.Null(strayMember.EffectiveCategoryId);
        Assert.Null(uncatMember.CategoryId);
        Assert.Null(uncatMember.EffectiveCategoryId);
    }

    // ── TC-BUDGET-ENVELOPE-REPARENT-RELENSES ──────────────────────────────────

    [Fact]
    public async Task Reparent_relenses_later_position_under_new_snapshot_same_revision()
    {
        var parentA = await CreateCategoryAsync("ParentA");
        var parentB = await CreateCategoryAsync("ParentB");
        var child = await CreateCategoryAsync("MovableChild", parentA.CategoryId);

        var draft = await CreateDraftAsync(
            Period(2026, 7),
            [Entry(parentA.CategoryId, 100_000), Entry(parentB.CategoryId, 200_000)],
            "reparent plan");
        var activated = await ActivateAsync(draft.RevisionId, "act");

        var tx = await RecordAsync("-5.00", "2026-07-08", "reparent-tx");
        await AssignCategoryAsync(tx.TransactionId, child.CategoryId);

        var first = await GetPositionSuccessAsync(Period(2026, 7));
        Assert.NotNull(first.Position);
        var firstPos = first.Position!;
        Assert.Equal(activated.RevisionId, firstPos.RevisionId);
        Assert.Equal(BudgetRevisionStatus.Active, firstPos.RevisionStatus);

        var firstA = firstPos.CategoryPositions.Single(p => p.CategoryId == parentA.CategoryId);
        Assert.Equal(500, firstA.ActualMinorUnits);
        Assert.Equal(500, firstA.DescendantActualMinorUnits);
        Assert.Equal([child.CategoryId], firstA.AbsorbedCategoryIds);

        var firstB = firstPos.CategoryPositions.Single(p => p.CategoryId == parentB.CategoryId);
        Assert.Equal(0, firstB.ActualMinorUnits);
        var firstSnapshot = firstPos.Ledger.SnapshotId;

        await ReparentCategoryAsync(child.CategoryId, parentB.CategoryId, "move under B");

        var second = await GetPositionSuccessAsync(Period(2026, 7));
        Assert.NotNull(second.Position);
        var secondPos = second.Position!;

        // Bound revision unchanged; new LEDGER snapshot after reparent.
        Assert.Equal(activated.RevisionId, secondPos.RevisionId);
        Assert.Equal(BudgetRevisionStatus.Active, secondPos.RevisionStatus);
        Assert.NotEqual(firstSnapshot, secondPos.Ledger.SnapshotId);

        var secondA = secondPos.CategoryPositions.Single(p => p.CategoryId == parentA.CategoryId);
        Assert.Equal(0, secondA.ActualMinorUnits);
        Assert.Equal(0, secondA.DescendantActualMinorUnits);
        Assert.True(secondA.AbsorbedCategoryIds is null || secondA.AbsorbedCategoryIds.Count == 0);

        var secondB = secondPos.CategoryPositions.Single(p => p.CategoryId == parentB.CategoryId);
        Assert.Equal(500, secondB.ActualMinorUnits);
        Assert.Equal(500, secondB.DescendantActualMinorUnits);
        Assert.Equal([child.CategoryId], secondB.AbsorbedCategoryIds);

        // INSIGHTS member effective category follows the new ancestry under a fresh snapshot.
        // Each position/evidence get materializes its own snapshot id (A4: determinism within one
        // cited snapshot, not across independent reads).
        var evidence = await GetEvidenceSuccessAsync(Period(2026, 7));
        var member = evidence.ActualMembers.Single(m => m.TransactionId == tx.TransactionId);
        Assert.Equal([parentB.CategoryId, child.CategoryId], member.AncestryIds);
        Assert.Equal(parentB.CategoryId, member.EffectiveCategoryId);
        Assert.NotEqual(firstSnapshot, evidence.Ledger.SnapshotId);
        var evidenceB = evidence.Position!.CategoryPositions.Single(p => p.CategoryId == parentB.CategoryId);
        Assert.Equal(500, evidenceB.ActualMinorUnits);
        Assert.Equal([child.CategoryId], evidenceB.AbsorbedCategoryIds);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<GetBudgetPositionResult> GetPositionSuccessAsync(BudgetPeriodInput period)
    {
        var input =
            "{\"contractVersion\":\"1.0\",\"period\":{\"year\":"
            + period.Year.ToString(CultureInfo.InvariantCulture)
            + ",\"month\":"
            + period.Month.ToString(CultureInfo.InvariantCulture)
            + ",\"currencyCode\":\""
            + period.CurrencyCode
            + "\"},\"revisionId\":null}";
        var processResult = await process.RunAsync(
            ["budget", "position", "get", "--input", "-"],
            Envelope(input, idempotencyKey: null),
            CancellationToken.None);
        var parsed = ParseResult(processResult, BudgetJsonContext.Default.GetBudgetPositionResult);
        Assert.Equal(0, parsed.ExitCode);
        Assert.NotNull(parsed.Value);
        return parsed.Value!;
    }

    private async Task<BudgetInsightEvidence> GetEvidenceSuccessAsync(BudgetPeriodInput period)
    {
        var input =
            "{\"contractVersion\":\"1.0\",\"budgetPeriod\":{\"year\":"
            + period.Year.ToString(CultureInfo.InvariantCulture)
            + ",\"month\":"
            + period.Month.ToString(CultureInfo.InvariantCulture)
            + ",\"currencyCode\":\""
            + period.CurrencyCode
            + "\"}}";
        var processResult = await process.RunAsync(
            ["budget", "insights", "evidence", "get", "--input", "-"],
            Envelope(input, idempotencyKey: null),
            CancellationToken.None);
        var parsed = ParseResult(processResult, BudgetJsonContext.Default.GetBudgetInsightEvidenceResult);
        Assert.Equal(0, parsed.ExitCode);
        Assert.NotNull(parsed.Value);
        return parsed.Value!.Evidence;
    }

    private async Task<DraftCreated> CreateDraftAsync(
        BudgetPeriodInput period,
        IReadOnlyList<BudgetPlanEntryInput> entries,
        string reason)
    {
        var entriesJson = string.Join(
            ",",
            entries.Select(e =>
                "{\"categoryId\":\""
                + e.CategoryId
                + "\",\"plannedMinorUnits\":"
                + e.PlannedMinorUnits.ToString(CultureInfo.InvariantCulture)
                + "}"));
        var input =
            "{\"contractVersion\":\"1.0\",\"period\":{\"year\":"
            + period.Year.ToString(CultureInfo.InvariantCulture)
            + ",\"month\":"
            + period.Month.ToString(CultureInfo.InvariantCulture)
            + ",\"currencyCode\":\""
            + period.CurrencyCode
            + "\"},\"entries\":["
            + entriesJson
            + "],\"reason\":\""
            + reason
            + "\"}";
        var processResult = await process.RunAsync(
            ["budget", "plan", "draft", "create", "--input", "-"],
            Envelope(input, NextKey()),
            CancellationToken.None);
        Assert.Equal(0, processResult.ExitCode);
        using var document = JsonDocument.Parse(processResult.Stdout);
        var revision = document.RootElement.GetProperty("result").GetProperty("revision");
        return new DraftCreated(
            revision.GetProperty("planId").GetString()!,
            revision.GetProperty("revisionId").GetString()!);
    }

    private async Task<ActivatedRevision> ActivateAsync(string revisionId, string reason)
    {
        var input =
            "{\"contractVersion\":\"1.0\",\"revisionId\":\""
            + revisionId
            + "\",\"reason\":\""
            + reason
            + "\"}";
        var processResult = await process.RunAsync(
            ["budget", "plan", "revision", "activate", "--input", "-"],
            Envelope(input, NextKey()),
            CancellationToken.None);
        Assert.Equal(0, processResult.ExitCode);
        using var document = JsonDocument.Parse(processResult.Stdout);
        var activated = document.RootElement.GetProperty("result").GetProperty("activated");
        return new ActivatedRevision(
            activated.GetProperty("planId").GetString()!,
            activated.GetProperty("revisionId").GetString()!);
    }

    private static BudgetPeriodInput Period(int year, int month) => new(year, month, "ZAR");

    private static BudgetPlanEntryInput Entry(string categoryId, long amount) => new(categoryId, amount);

    private string Envelope(string inputJson, string? idempotencyKey) =>
        idempotencyKey is null
            ? "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"budget-env-prov\",\"runId\":\"run-01\"},\"input\":" + inputJson + "}"
            : "{\"contractVersion\":\"1.0\",\"actor\":{\"kind\":\"automation\",\"label\":\"budget-env-prov\",\"runId\":\"run-01\"},\"idempotencyKey\":\"" + idempotencyKey + "\",\"input\":" + inputJson + "}";

    private async Task<AccountDetail> CreateAccountAsync()
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        return await ExecuteSuccessAsync(
            "ledger.account.create",
            new CreateAccountInput(
                $"Budget EnvProv Bank {unique}",
                $"Primary-{unique}",
                AccountType.Cheque,
                $"****{Random.Shared.Next(1000, 9999)}",
                "ZAR"),
            NextKey(),
            LedgerJsonContext.Default.CreateAccountInput,
            LedgerJsonContext.Default.AccountDetail);
    }

    private Task<CategoryDetail> CreateCategoryAsync(string name, string? parentCategoryId = null) =>
        ExecuteSuccessAsync(
            "ledger.category.create",
            new CreateCategoryInput(name, parentCategoryId),
            NextKey(),
            LedgerJsonContext.Default.CreateCategoryInput,
            LedgerJsonContext.Default.CategoryDetail);

    private async Task ReparentCategoryAsync(string categoryId, string parentCategoryId, string reason) =>
        _ = await ExecuteSuccessAsync(
            "ledger.category.reparent",
            new ReparentCategoryInput(categoryId, parentCategoryId, reason),
            NextKey(),
            LedgerJsonContext.Default.ReparentCategoryInput,
            LedgerJsonContext.Default.CategoryReparentResult);

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
            new AssignCategoryInput(transactionId, categoryId, "budget-env-prov"),
            "cat-" + transactionId + "-" + Guid.NewGuid().ToString("N")[..6],
            LedgerJsonContext.Default.AssignCategoryInput,
            LedgerJsonContext.Default.CategoryAllocationResult);

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

    private static ProcessOpResult<T> ParseResult<T>(
        ProcessResult processResult,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> resultType)
    {
        using var document = JsonDocument.Parse(processResult.Stdout);
        var rootEl = document.RootElement;
        if (processResult.ExitCode != 0)
        {
            var error = rootEl.GetProperty("error");
            return new(
                processResult.ExitCode,
                default,
                error.GetProperty("code").GetString(),
                error.GetProperty("category").GetString());
        }

        var value = JsonSerializer.Deserialize(rootEl.GetProperty("result").GetRawText(), resultType)!;
        return new(processResult.ExitCode, value, null, null);
    }

    private string NextKey() => $"budget-env-prov-{Interlocked.Increment(ref keySeq):D4}";

    private sealed record DraftCreated(string PlanId, string RevisionId);

    private sealed record ActivatedRevision(string PlanId, string RevisionId);

    private sealed record ProcessOpResult<T>(
        int ExitCode,
        T? Value,
        string? ErrorCode,
        string? ErrorCategory);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
