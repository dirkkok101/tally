using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Transactions;
using Tally.Features.Ledger.Transactions;
using Tally.Infrastructure.Storage;
using Xunit;

namespace Tally.Tests.Classify.LedgerContract;

/// <summary>
/// DD-CLASSIFY-LEDGER-PUBLIC-PROJECTION / LedgerCategoryMutationPreconditions
/// Atomic revision and allocation identity checks on assign/correct.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LedgerClassificationMutationPreconditionTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-classify-mut-" + Guid.NewGuid().ToString("N"));
    private TallyProcess process = null!;
    private int keySeq;

    public async Task InitializeAsync()
    {
        var database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        process = new TallyProcess(OperationRegistry.Create(), LedgerServices.Create(database));
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public void TC_CLASSIFY_MUTATION_registry_declares_stale_precondition()
    {
        var registry = OperationRegistry.Create();
        foreach (var operationId in new[] { "ledger.transaction.category.assign", "ledger.transaction.category.correct" })
        {
            var descriptor = registry.Find(operationId)!;
            Assert.Contains(descriptor.DomainErrors!, error => error.Code == CategoryAllocationErrors.StalePrecondition);
        }
    }

    [Fact]
    public async Task TC_CLASSIFY_MUTATION_legacy_assign_without_preconditions_still_works()
    {
        var account = await CreateAccount();
        var cat = await CreateCategory("Legacy");
        var tx = await Record(account.AccountId, 'a');
        var result = Allocation(await Assign(new AssignCategoryInput(tx.TransactionId, cat.CategoryId, "owner"), "legacy-assign"));
        Assert.Equal(cat.CategoryId, result.Transaction.Category.CategoryId);
    }

    [Fact]
    public async Task TC_CLASSIFY_MUTATION_assign_with_existing_allocation_is_cardinality()
    {
        var account = await CreateAccount();
        var first = await CreateCategory("First");
        var second = await CreateCategory("Second");
        var tx = await Record(account.AccountId, 'b');
        await Assign(new AssignCategoryInput(tx.TransactionId, first.CategoryId, "first"), "assign-b1");
        AssertError(await Assign(new AssignCategoryInput(tx.TransactionId, second.CategoryId, "split"), "assign-b2"), 5, CategoryAllocationErrors.Cardinality);
    }

    [Fact]
    public async Task TC_CLASSIFY_MUTATION_assign_with_expected_active_allocation_id_is_stale()
    {
        var account = await CreateAccount();
        var cat = await CreateCategory("StaleAssign");
        var tx = await Record(account.AccountId, 'c');
        AssertError(
            await Assign(new AssignCategoryInput(
                tx.TransactionId, cat.CategoryId, "owner",
                ExpectedActiveAllocationId: LedgerId.New().ToString()), "stale-assign"),
            5,
            CategoryAllocationErrors.StalePrecondition);
    }

    [Fact]
    public async Task TC_CLASSIFY_MUTATION_assign_with_matching_none_allocation_revision_succeeds()
    {
        var account = await CreateAccount();
        var cat = await CreateCategory("OkAssign");
        var tx = await Record(account.AccountId, 'd');
        var preflight = await Preflight(tx.TransactionId);
        var item = Assert.Single(preflight.ClassificationItems!);
        Assert.Equal("none", item.AllocationRevision);

        var result = Allocation(await Assign(new AssignCategoryInput(
            tx.TransactionId,
            cat.CategoryId,
            "owner",
            ExpectedTransactionRevision: item.TransactionRevision,
            ExpectedRelationshipRevision: item.RelationshipRevision,
            ExpectedAllocationRevision: item.AllocationRevision), "ok-assign"));
        Assert.Equal(cat.CategoryId, result.Transaction.Category.CategoryId);
    }

    [Fact]
    public async Task TC_CLASSIFY_MUTATION_assign_with_drifted_allocation_revision_is_stale()
    {
        var account = await CreateAccount();
        var cat = await CreateCategory("DriftAssign");
        var tx = await Record(account.AccountId, 'e');
        AssertError(
            await Assign(new AssignCategoryInput(
                tx.TransactionId, cat.CategoryId, "owner",
                ExpectedAllocationRevision: "not-the-none-token"), "drift-alloc"),
            5,
            CategoryAllocationErrors.StalePrecondition);
    }

    [Fact]
    public async Task TC_CLASSIFY_MUTATION_assign_with_drifted_transaction_revision_is_stale()
    {
        var account = await CreateAccount();
        var cat = await CreateCategory("DriftTxn");
        var tx = await Record(account.AccountId, 'f');
        AssertError(
            await Assign(new AssignCategoryInput(
                tx.TransactionId, cat.CategoryId, "owner",
                ExpectedTransactionRevision: "genesis:not-this-id"), "drift-txn"),
            5,
            CategoryAllocationErrors.StalePrecondition);
    }

    [Fact]
    public async Task TC_CLASSIFY_MUTATION_correct_with_matching_preconditions_succeeds()
    {
        var account = await CreateAccount();
        var first = await CreateCategory("Original");
        var second = await CreateCategory("Replacement");
        var tx = await Record(account.AccountId, 'g');
        await Assign(new AssignCategoryInput(tx.TransactionId, first.CategoryId, "initial"), "assign-g");
        var preflight = await Preflight(tx.TransactionId);
        var item = Assert.Single(preflight.ClassificationItems!);

        var corrected = Allocation(await Correct(new CorrectCategoryInput(
            tx.TransactionId,
            second.CategoryId,
            "owner corrected",
            ExpectedActiveAllocationId: item.CurrentAllocationId,
            ExpectedTransactionRevision: item.TransactionRevision,
            ExpectedRelationshipRevision: item.RelationshipRevision,
            ExpectedAllocationRevision: item.AllocationRevision), "correct-g"));
        Assert.Equal(second.CategoryId, corrected.Transaction.Category.CategoryId);
    }

    [Fact]
    public async Task TC_CLASSIFY_MUTATION_correct_with_wrong_allocation_id_is_stale()
    {
        var account = await CreateAccount();
        var first = await CreateCategory("A");
        var second = await CreateCategory("B");
        var tx = await Record(account.AccountId, 'h');
        await Assign(new AssignCategoryInput(tx.TransactionId, first.CategoryId, "initial"), "assign-h");

        AssertError(
            await Correct(new CorrectCategoryInput(
                tx.TransactionId, second.CategoryId, "owner corrected",
                ExpectedActiveAllocationId: LedgerId.New().ToString()), "correct-h"),
            5,
            CategoryAllocationErrors.StalePrecondition);
    }

    [Fact]
    public async Task TC_CLASSIFY_MUTATION_correct_after_intervening_correction_is_stale()
    {
        var account = await CreateAccount();
        var first = await CreateCategory("One");
        var second = await CreateCategory("Two");
        var third = await CreateCategory("Three");
        var tx = await Record(account.AccountId, 'i');
        await Assign(new AssignCategoryInput(tx.TransactionId, first.CategoryId, "initial"), "assign-i");
        var preflight = await Preflight(tx.TransactionId);
        var item = Assert.Single(preflight.ClassificationItems!);

        // Intervening correction changes allocation identity.
        await Correct(new CorrectCategoryInput(tx.TransactionId, second.CategoryId, "first correction"), "correct-i1");

        AssertError(
            await Correct(new CorrectCategoryInput(
                tx.TransactionId,
                third.CategoryId,
                "stale correction",
                ExpectedActiveAllocationId: item.CurrentAllocationId,
                ExpectedAllocationRevision: item.AllocationRevision), "correct-i2"),
            5,
            CategoryAllocationErrors.StalePrecondition);
    }

    [Fact]
    public async Task TC_CLASSIFY_MUTATION_correct_without_preconditions_preserves_legacy_behavior()
    {
        var account = await CreateAccount();
        var first = await CreateCategory("LegacyFrom");
        var second = await CreateCategory("LegacyTo");
        var tx = await Record(account.AccountId, 'j');
        await Assign(new AssignCategoryInput(tx.TransactionId, first.CategoryId, "initial"), "assign-j");
        var corrected = Allocation(await Correct(new CorrectCategoryInput(tx.TransactionId, second.CategoryId, "owner"), "correct-j"));
        Assert.Equal(second.CategoryId, corrected.Transaction.Category.CategoryId);
    }

    [Fact]
    public async Task TC_CLASSIFY_MUTATION_stale_precondition_appends_no_allocation()
    {
        var account = await CreateAccount();
        var first = await CreateCategory("CountFrom");
        var second = await CreateCategory("CountTo");
        var tx = await Record(account.AccountId, 'k');
        await Assign(new AssignCategoryInput(tx.TransactionId, first.CategoryId, "initial"), "assign-k");
        var before = await GetTransaction(tx.TransactionId);

        AssertError(
            await Correct(new CorrectCategoryInput(
                tx.TransactionId, second.CategoryId, "stale",
                ExpectedActiveAllocationId: "not-an-allocation"), "correct-k"),
            5,
            CategoryAllocationErrors.StalePrecondition);

        var after = await GetTransaction(tx.TransactionId);
        Assert.Equal(before.Category.AllocationEventId, after.Category.AllocationEventId);
        Assert.Equal(first.CategoryId, after.Category.CategoryId);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<ActualsQueryResult> Preflight(string transactionId)
    {
        var input = new QueryActualsInput(
            Purpose: ClassificationProjectionPurpose.ApplyPreflight,
            ItemProjection: ClassificationProjectionVersions.ClassificationV1,
            TransactionIds: [transactionId]);
        var result = await Run(
            "ledger.actuals.query",
            JsonSerializer.SerializeToElement(input, ActualsJsonContext.Default.QueryActualsInput),
            key: null);
        return Success(result, ActualsJsonContext.Default.ActualsQueryResult);
    }

    private async Task<AccountDetail> CreateAccount()
    {
        var input = new CreateAccountInput("Mut Bank", "Primary", AccountType.Cheque, "****9999", "ZAR");
        return Success(await Run("ledger.account.create", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.CreateAccountInput), NextKey()), LedgerJsonContext.Default.AccountDetail);
    }

    private async Task<CategoryDetail> CreateCategory(string name)
    {
        var input = new CreateCategoryInput(name);
        return Success(await Run("ledger.category.create", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.CreateCategoryInput), NextKey()), LedgerJsonContext.Default.CategoryDetail);
    }

    private async Task<TransactionDetail> Record(string accountId, char digest)
    {
        var digestText = string.Concat(Enumerable.Repeat(((byte)digest).ToString("x2", System.Globalization.CultureInfo.InvariantCulture), 32));
        var input = new RecordTransactionInput(
            accountId, "-12.34", "ZAR", "2026-07-15", null, "Mut purchase " + digest, null, null,
            new RegisterEvidenceInput(EvidenceKind.AgentCapture, digestText, "mut-capture:" + digest, null, null));
        return Success(await Run("ledger.transaction.record", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.RecordTransactionInput), NextKey()), LedgerJsonContext.Default.TransactionDetail);
    }

    private Task<ProcessResult> Assign(AssignCategoryInput input, string key) =>
        Run("ledger.transaction.category.assign", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.AssignCategoryInput), key);

    private Task<ProcessResult> Correct(CorrectCategoryInput input, string key) =>
        Run("ledger.transaction.category.correct", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.CorrectCategoryInput), key);

    private async Task<TransactionDetail> GetTransaction(string transactionId)
    {
        var input = new GetTransactionInput(transactionId, true);
        return Success(await Run("ledger.transaction.get", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.GetTransactionInput), key: null), LedgerJsonContext.Default.TransactionDetail);
    }

    private async Task<ProcessResult> Run(string operationId, JsonElement input, string? key)
    {
        var descriptor = OperationRegistry.Create().Find(operationId)!;
        var actor = new SafeActor("human", "classify-mut-test", "run-01");
        var body = JsonSerializer.Serialize(
            new RequestEnvelope("1.0", actor, input, key),
            LedgerJsonContext.Default.RequestEnvelope);
        var args = descriptor.CliPath.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1).Concat(["--input", "-"]).ToArray();
        return await process.RunAsync(args, body, CancellationToken.None);
    }

    private static CategoryAllocationResult Allocation(ProcessResult result) =>
        Success(result, LedgerJsonContext.Default.CategoryAllocationResult);

    private static T Success<T>(ProcessResult result, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        return JsonSerializer.Deserialize(document.RootElement.GetProperty("result").GetRawText(), typeInfo)!;
    }

    private static void AssertError(ProcessResult result, int exitCode, string errorCode)
    {
        Assert.Equal(exitCode, result.ExitCode);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(errorCode, document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private string NextKey() => "classify-mut-" + Interlocked.Increment(ref keySeq).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
}
