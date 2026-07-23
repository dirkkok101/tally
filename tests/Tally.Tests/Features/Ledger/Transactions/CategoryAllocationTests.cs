using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Domain.Ledger.Transactions;
using Tally.Features.Ledger.Categories;
using Tally.Features.Ledger.Transactions;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Transactions;
using Xunit;

namespace Tally.Tests.Features.Ledger.Transactions;

[SupportedOSPlatform("linux")]
// Covers TC-LEDGER-CATEGORY-ASSIGNMENT-CONTRACT.
public sealed class CategoryAllocationTests : IAsyncLifetime
{
    private const string At = "2026-07-22T00:00:00.0000000Z";
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-category-allocation-{Guid.NewGuid():N}");
    private TallyProcess process = null!;
    private LedgerDb database = null!;
    private CategoryAllocationStore store = null!;

    [Fact]
    public void DM_LEDGER_TRANSACTION_CONTRACTS_registry_exposes_assign_and_correct()
    {
        var registry = OperationRegistry.Create();

        Assert.Equal(typeof(AssignCategoryInput), registry.Find("ledger.transaction.category.assign")!.RequestTypeInfo.Type);
        Assert.Equal(typeof(CorrectCategoryInput), registry.Find("ledger.transaction.category.correct")!.RequestTypeInfo.Type);
        Assert.All(
            new[] { "ledger.transaction.category.assign", "ledger.transaction.category.correct" },
            operation => Assert.Equal(typeof(CategoryAllocationResult), registry.Find(operation)!.ResultTypeInfo.Type));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FR_LEDGER_CATEGORY_ASSIGNMENT_assigns_any_active_hierarchy_node_once(int targetIndex)
    {
        var account = await CreateAccount();
        var transaction = await Record(account.AccountId, 'a');
        var parent = await CreateCategory("Living", null, "parent");
        var intermediate = await CreateCategory("Food", parent.CategoryId, "intermediate");
        var leaf = await CreateCategory("Groceries", intermediate.CategoryId, "leaf");
        var target = new[] { parent, intermediate, leaf }[targetIndex];

        var result = Allocation(await Assign(transaction.TransactionId, target.CategoryId, "owner classification", "assign"));

        Assert.Equal(target.CategoryId, result.Transaction.Category.CategoryId);
        Assert.Equal(result.AllocationEventId, result.Transaction.Category.AllocationEventId);
        Assert.Equal(target.AncestryIds, result.Transaction.Category.CurrentAncestryIds);
        Assert.Equal(transaction.SignedAmount, result.Transaction.SignedAmount);
        Assert.Equal(TransactionPoolState.Unassigned, result.Transaction.Pool.State);
        Assert.Equal(TransactionKnowledgeState.Unknown, result.Transaction.PaymentAttribution.InstrumentState);
        Assert.Equal(TransactionReconciliationState.RecordedUnreconciled, result.Transaction.ReconciliationState);
        Assert.Single(result.Transaction.History!.CategoryAssignments);
    }

    [Fact]
    public async Task FR_LEDGER_CATEGORY_ASSIGNMENT_correction_appends_attributable_history_and_changes_only_category()
    {
        var account = await CreateAccount();
        var transaction = await Record(account.AccountId, 'b');
        var original = await CreateCategory("Travel", null, "original");
        var replacement = await CreateCategory("Work travel", null, "replacement");
        var assigned = Allocation(await Assign(transaction.TransactionId, original.CategoryId, "initial choice", "assign"));

        var corrected = Allocation(await Correct(transaction.TransactionId, replacement.CategoryId, "owner corrected", "correct"));

        Assert.NotEqual(assigned.AllocationEventId, corrected.AllocationEventId);
        Assert.Equal(replacement.CategoryId, corrected.Transaction.Category.CategoryId);
        Assert.Equal(transaction.AccountId, corrected.Transaction.AccountId);
        Assert.Equal(transaction.SignedAmount, corrected.Transaction.SignedAmount);
        Assert.Equal(transaction.Evidence.Single().EvidenceId, corrected.Transaction.Evidence.Single().EvidenceId);
        Assert.Equal(TransactionPoolState.Unassigned, corrected.Transaction.Pool.State);
        Assert.Equal(TransactionKnowledgeState.Unknown, corrected.Transaction.PaymentAttribution.CardholderState);
        Assert.Equal(TransactionReconciliationState.RecordedUnreconciled, corrected.Transaction.ReconciliationState);
        var history = corrected.Transaction.History!.CategoryAssignments;
        Assert.Collection(
            history,
            item =>
            {
                Assert.Equal(TransactionCategoryAction.Assign, item.Action);
                Assert.Null(item.PreviousEventId);
                Assert.Equal("initial choice", item.Reason);
            },
            item =>
            {
                Assert.Equal(TransactionCategoryAction.Correct, item.Action);
                Assert.Equal(assigned.AllocationEventId, item.PreviousEventId);
                Assert.Equal("owner corrected", item.Reason);
                Assert.Equal("human:category-allocation-test", item.Actor);
            });
    }

    [Fact]
    public async Task FR_LEDGER_CATEGORY_ASSIGNMENT_split_assignment_is_a_stable_cardinality_conflict()
    {
        var account = await CreateAccount();
        var transaction = await Record(account.AccountId, 'c');
        var first = await CreateCategory("First", null, "first");
        var second = await CreateCategory("Second", null, "second");
        await Assign(transaction.TransactionId, first.CategoryId, "first", "assign");

        AssertError(await Assign(transaction.TransactionId, second.CategoryId, "split", "split"), 5, CategoryAllocationErrors.Cardinality);
        Assert.Equal(first.CategoryId, (await GetTransaction(transaction.TransactionId, true)).Category.CategoryId);
        Assert.Equal(1, await Count("category_allocation_event"));
    }

    [Fact]
    public async Task FR_LEDGER_CATEGORY_ASSIGNMENT_concurrent_split_assignment_commits_exactly_one_category()
    {
        var account = await CreateAccount();
        var transaction = await Record(account.AccountId, 't');
        var first = await CreateCategory("Concurrent first", null, "first");
        var second = await CreateCategory("Concurrent second", null, "second");

        var results = await Task.WhenAll(
            Assign(transaction.TransactionId, first.CategoryId, "first concurrent choice", "assign-first"),
            Assign(transaction.TransactionId, second.CategoryId, "second concurrent choice", "assign-second"));

        Assert.Single(results, result => result.ExitCode == 0);
        var conflict = Assert.Single(results, result => result.ExitCode != 0);
        AssertError(conflict, 5, CategoryAllocationErrors.Cardinality);
        Assert.Equal(1, await Count("category_allocation_event"));
    }

    [Fact]
    public async Task FR_LEDGER_CATEGORY_ASSIGNMENT_correction_requires_a_current_assignment_and_a_change()
    {
        var account = await CreateAccount();
        var transaction = await Record(account.AccountId, 'd');
        var category = await CreateCategory("Only", null, "category");

        AssertError(await Correct(transaction.TransactionId, category.CategoryId, "missing", "missing"), 6, CategoryAllocationErrors.NotAssigned);
        await Assign(transaction.TransactionId, category.CategoryId, "assigned", "assign");
        AssertError(await Correct(transaction.TransactionId, category.CategoryId, "same", "same"), 5, CategoryAllocationErrors.Unchanged);
        Assert.Equal(1, await Count("category_allocation_event"));
    }

    [Fact]
    public async Task FR_LEDGER_CATEGORY_ASSIGNMENT_inactive_transaction_changes_nothing()
    {
        var account = await CreateAccount();
        var transaction = await Record(account.AccountId, 'e');
        var category = await CreateCategory("Category", null, "category");
        await Terminate(transaction.TransactionId, "void", null, null);

        AssertError(await Assign(transaction.TransactionId, category.CategoryId, "late", "late"), 6, CategoryAllocationErrors.TransactionInactive);
        Assert.Equal(0, await Count("category_allocation_event"));
    }

    [Fact]
    public async Task FR_LEDGER_CATEGORY_ASSIGNMENT_archived_category_changes_nothing()
    {
        var account = await CreateAccount();
        var transaction = await Record(account.AccountId, 'f');
        var category = await CreateCategory("Archived", null, "category");
        await ArchiveCategory(category.CategoryId);

        AssertError(await Assign(transaction.TransactionId, category.CategoryId, "late", "late"), 6, CategoryErrors.Archived);
        Assert.Equal(0, await Count("category_allocation_event"));
    }

    [Fact]
    public async Task FR_LEDGER_CATEGORY_ASSIGNMENT_missing_transaction_or_category_is_stable()
    {
        var account = await CreateAccount();
        var transaction = await Record(account.AccountId, 'g');
        var category = await CreateCategory("Known", null, "category");

        AssertError(await Assign(LedgerId.New().ToString(), category.CategoryId, "missing transaction", "tx"), 4, TransactionErrors.NotFound);
        AssertError(await Assign(transaction.TransactionId, LedgerId.New().ToString(), "missing category", "category-missing"), 4, CategoryErrors.NotFound);
        Assert.Equal(0, await Count("category_allocation_event"));
    }

    [Fact]
    public async Task FR_LEDGER_CATEGORY_ASSIGNMENT_replay_returns_original_and_changed_input_conflicts()
    {
        var account = await CreateAccount();
        var transaction = await Record(account.AccountId, 'h');
        var first = await CreateCategory("First", null, "first");
        var second = await CreateCategory("Second", null, "second");
        var input = new AssignCategoryInput(transaction.TransactionId, first.CategoryId, "owner choice");
        var original = Allocation(await Assign(input, "same"));

        Assert.Equal(original.AllocationEventId, Allocation(await Assign(input, "same")).AllocationEventId);
        AssertError(await Assign(input with { CategoryId = second.CategoryId }, "same"), 5, "LEDGER-IDEMPOTENCY-001");
        Assert.Equal(1, await Count("category_allocation_event"));
    }

    [Fact]
    public async Task FR_LEDGER_CATEGORY_ASSIGNMENT_correction_replay_returns_the_original_event()
    {
        var account = await CreateAccount();
        var transaction = await Record(account.AccountId, 'u');
        var first = await CreateCategory("Replay first", null, "first");
        var second = await CreateCategory("Replay second", null, "second");
        await Assign(transaction.TransactionId, first.CategoryId, "initial", "assign");

        var original = Allocation(await Correct(transaction.TransactionId, second.CategoryId, "owner correction", "correct"));
        var replay = Allocation(await Correct(transaction.TransactionId, second.CategoryId, "owner correction", "correct"));

        Assert.Equal(original.AllocationEventId, replay.AllocationEventId);
        Assert.Equal(2, await Count("category_allocation_event"));
    }

    [Theory]
    [InlineData("", "reason")]
    [InlineData("invalid", "transaction")]
    [InlineData("invalid", "category")]
    public async Task FR_LEDGER_CATEGORY_ASSIGNMENT_invalid_input_is_atomic(string invalid, string target)
    {
        var account = await CreateAccount();
        var transaction = await Record(account.AccountId, 'v');
        var category = await CreateCategory("Validation", null, "category");
        var input = new AssignCategoryInput(
            target == "transaction" ? invalid : transaction.TransactionId,
            target == "category" ? invalid : category.CategoryId,
            target == "reason" ? invalid : "valid reason");

        AssertError(await Assign(input, "invalid-" + target), 3, CategoryAllocation.InvalidError);
        Assert.Equal(0, await Count("category_allocation_event"));
    }

    [Fact]
    public async Task DD_LEDGER_CATEGORY_HIERARCHY_reparent_preserves_assignment_identity_and_resolves_current_ancestry()
    {
        var account = await CreateAccount();
        var transaction = await Record(account.AccountId, 'i');
        var oldParent = await CreateCategory("Old parent", null, "old-parent");
        var newParent = await CreateCategory("New parent", null, "new-parent");
        var child = await CreateCategory("Child", oldParent.CategoryId, "child");
        var assigned = Allocation(await Assign(transaction.TransactionId, child.CategoryId, "owner choice", "assign"));

        await ReparentCategory(child.CategoryId, newParent.CategoryId);
        var current = await GetTransaction(transaction.TransactionId, true);

        Assert.Equal(assigned.AllocationEventId, current.Category.AllocationEventId);
        Assert.Equal(new[] { newParent.CategoryId, child.CategoryId }, current.Category.CurrentAncestryIds);
        Assert.Single(current.History!.CategoryAssignments);
        Assert.Equal(1, await Count("category_allocation_event"));
    }

    [Fact]
    public async Task DD_LEDGER_CATEGORY_HIERARCHY_direct_subtree_and_all_membership_never_duplicate()
    {
        var account = await CreateAccount();
        var rootCategory = await CreateCategory("Root", null, "root-category");
        var child = await CreateCategory("Child", rootCategory.CategoryId, "child");
        var sibling = await CreateCategory("Sibling", rootCategory.CategoryId, "sibling");
        var first = await Record(account.AccountId, 'j');
        var second = await Record(account.AccountId, 'k');
        var third = await Record(account.AccountId, 'l');
        await Assign(first.TransactionId, child.CategoryId, "child one", "assign-one");
        await Assign(second.TransactionId, child.CategoryId, "child two", "assign-two");
        await Assign(third.TransactionId, sibling.CategoryId, "sibling", "assign-three");

        Assert.Equal(new[] { first.TransactionId, second.TransactionId }.Order(), await store.ListDirectMemberTransactionIdsAsync(child.CategoryId, CancellationToken.None));
        Assert.Equal(new[] { first.TransactionId, second.TransactionId }.Order(), await store.ListSubtreeMemberTransactionIdsAsync(child.CategoryId, CancellationToken.None));
        Assert.Equal(new[] { first.TransactionId, second.TransactionId, third.TransactionId }.Order(), await store.ListSubtreeMemberTransactionIdsAsync(rootCategory.CategoryId, CancellationToken.None));
        var all = await store.ListAllMemberTransactionIdsAsync(CancellationToken.None);
        Assert.Equal(3, all.Count);
        Assert.Equal(3, all.Distinct().Count());
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_authorized_statement_correction_carries_category_explicitly()
    {
        var account = await CreateAccount();
        var source = await Record(account.AccountId, 'm');
        var replacement = await Record(account.AccountId, 'n');
        var category = await CreateCategory("Carried", null, "category");
        await Assign(source.TransactionId, category.CategoryId, "source", "assign");
        var decisionId = await AuthorizeStatementCorrection(source.TransactionId, replacement.TransactionId);

        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        var eventId = await store.CarryForwardAsync(
            connection, transaction, source.TransactionId, replacement.TransactionId, decisionId,
            "statement correction", "system:reconciliation", At, CancellationToken.None);
        await transaction.CommitAsync();

        Assert.NotNull(eventId);
        var current = await GetTransaction(replacement.TransactionId, true);
        Assert.Equal(category.CategoryId, current.Category.CategoryId);
        var history = Assert.Single(current.History!.CategoryAssignments);
        Assert.Equal(TransactionCategoryAction.CarryForward, history.Action);
        Assert.Equal(source.TransactionId, history.SourceTransactionId);
        Assert.Equal(decisionId, history.ReconciliationDecisionId);
        Assert.Null(history.PreviousEventId);
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_unauthorized_carry_forward_is_rejected()
    {
        var account = await CreateAccount();
        var source = await Record(account.AccountId, 'o');
        var replacement = await Record(account.AccountId, 'p');
        var category = await CreateCategory("Private", null, "category");
        await Assign(source.TransactionId, category.CategoryId, "source", "assign");

        await using var connection = await Open();
        await using var transaction = connection.BeginTransaction();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CarryForwardAsync(
            connection, transaction, source.TransactionId, replacement.TransactionId, LedgerId.New().ToString(),
            "unauthorized", "system:test", At, CancellationToken.None));

        Assert.Equal(TransactionCategoryState.Uncategorized, (await GetTransaction(replacement.TransactionId, true)).Category.State);
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_ordinary_supersession_does_not_copy_category()
    {
        var account = await CreateAccount();
        var source = await Record(account.AccountId, 'q');
        var replacement = await Record(account.AccountId, 'r');
        var category = await CreateCategory("Source only", null, "category");
        await Assign(source.TransactionId, category.CategoryId, "source", "assign");

        await Terminate(source.TransactionId, "superseded", replacement.TransactionId, null);

        Assert.Equal(TransactionCategoryState.Uncategorized, (await GetTransaction(replacement.TransactionId, true)).Category.State);
        Assert.Equal(1, await Count("category_allocation_event"));
    }

    [Fact]
    public async Task DD_LEDGER_IMMUTABLE_HISTORY_allocation_events_reject_update_and_delete()
    {
        var account = await CreateAccount();
        var transaction = await Record(account.AccountId, 's');
        var category = await CreateCategory("Immutable", null, "category");
        await Assign(transaction.TransactionId, category.CategoryId, "owner", "assign");

        await using var connection = await Open();
        Assert.True((await Assert.ThrowsAsync<SqliteException>(() => Execute(connection, "UPDATE category_allocation_event SET reason = 'changed';"))).SqliteErrorCode > 0);
        Assert.True((await Assert.ThrowsAsync<SqliteException>(() => Execute(connection, "DELETE FROM category_allocation_event;"))).SqliteErrorCode > 0);
    }

    public async Task InitializeAsync()
    {
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        var factory = new LedgerConnectionFactory(new HostArtifactProtection());
        process = new TallyProcess(OperationRegistry.Create(), LedgerServices.Create(database));
        store = new CategoryAllocationStore(database, factory);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private async Task<AccountDetail> CreateAccount()
    {
        var input = new CreateAccountInput("Test Bank", "Primary", AccountType.Cheque, "****1234", "ZAR");
        return Success(await Run("ledger.account.create", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.CreateAccountInput), "account"), LedgerJsonContext.Default.AccountDetail);
    }

    private async Task<CategoryDetail> CreateCategory(string name, string? parentId, string key)
    {
        var input = new CreateCategoryInput(name, parentId);
        return Success(await Run("ledger.category.create", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.CreateCategoryInput), key), LedgerJsonContext.Default.CategoryDetail);
    }

    private Task<ProcessResult> ArchiveCategory(string categoryId) => Run(
        "ledger.category.archive",
        JsonSerializer.SerializeToElement(new ArchiveCategoryInput(categoryId, "archive for test"), LedgerJsonContext.Default.ArchiveCategoryInput),
        "archive-category");

    private Task<ProcessResult> ReparentCategory(string categoryId, string parentId) => Run(
        "ledger.category.reparent",
        JsonSerializer.SerializeToElement(new ReparentCategoryInput(categoryId, parentId, "move for current roll-up"), LedgerJsonContext.Default.ReparentCategoryInput),
        "reparent-category");

    private async Task<TransactionDetail> Record(string accountId, char digest)
    {
        var digestText = string.Concat(Enumerable.Repeat(((byte)digest).ToString("x2", System.Globalization.CultureInfo.InvariantCulture), 32));
        var input = new RecordTransactionInput(
            accountId, "-12.34", "ZAR", "2026-07-01", null, "Owner-safe purchase", null, null,
            new RegisterEvidenceInput(EvidenceKind.AgentCapture, digestText, "capture:" + digest, null, null));
        return Success(await Run("ledger.transaction.record", JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.RecordTransactionInput), "record-" + digest), LedgerJsonContext.Default.TransactionDetail);
    }

    private Task<ProcessResult> Assign(string transactionId, string categoryId, string reason, string key) =>
        Assign(new AssignCategoryInput(transactionId, categoryId, reason), key);

    private Task<ProcessResult> Assign(AssignCategoryInput input, string key) => Run(
        "ledger.transaction.category.assign",
        JsonSerializer.SerializeToElement(input, LedgerJsonContext.Default.AssignCategoryInput),
        key);

    private Task<ProcessResult> Correct(string transactionId, string categoryId, string reason, string key) => Run(
        "ledger.transaction.category.correct",
        JsonSerializer.SerializeToElement(new CorrectCategoryInput(transactionId, categoryId, reason), LedgerJsonContext.Default.CorrectCategoryInput),
        key);

    private async Task<TransactionDetail> GetTransaction(string transactionId, bool history) => Success(
        await Run("ledger.transaction.get", JsonSerializer.SerializeToElement(new GetTransactionInput(transactionId, history), LedgerJsonContext.Default.GetTransactionInput), null),
        LedgerJsonContext.Default.TransactionDetail);

    private async Task Terminate(string transactionId, string action, string? replacementId, string? decisionId)
    {
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO transaction_lifecycle_event (
                lifecycle_event_id, transaction_id, action, replacement_transaction_id,
                reconciliation_decision_id, reason, actor, occurred_at)
            VALUES ($eventId, $transactionId, $action, $replacementId, $decisionId, 'test termination', 'system:test', $at);
            """;
        command.Parameters.AddWithValue("$eventId", LedgerId.New().ToString());
        command.Parameters.AddWithValue("$transactionId", transactionId);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$replacementId", replacementId is null ? DBNull.Value : replacementId);
        command.Parameters.AddWithValue("$decisionId", decisionId is null ? DBNull.Value : decisionId);
        command.Parameters.AddWithValue("$at", At);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> AuthorizeStatementCorrection(string sourceId, string replacementId)
    {
        var evidenceId = LedgerId.New().ToString();
        var decisionId = LedgerId.New().ToString();
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO evidence_record VALUES ($evidenceId, 'statement_row', $digest, NULL, NULL, 'system:test', $at);
            INSERT INTO reconciliation_decision (
                decision_id, evidence_id, transaction_id, disposition, policy_id, policy_version,
                match_basis, deterministic, reason, decided_by, decided_at, previous_decision_id)
            VALUES ($decisionId, $evidenceId, $replacementId, 'replaced', NULL, NULL,
                    'statement authority', 0, 'corrected from statement', 'system:reconciliation', $at, NULL);
            INSERT INTO reconciliation_decision_authority (
                decision_id, disposition_detail, prior_transaction_id, active_transaction_id,
                authority_kind, statement_authority_basis, schema_origin, recorded_at)
            VALUES ($decisionId, 'corrected_from_statement', $sourceId, $replacementId,
                    'owner', 'statement row authority', 'v2', $at);
            INSERT INTO transaction_lifecycle_event (
                lifecycle_event_id, transaction_id, action, replacement_transaction_id,
                reconciliation_decision_id, reason, actor, occurred_at)
            VALUES ($lifecycleId, $sourceId, 'statement_authoritative_replacement', $replacementId,
                    $decisionId, 'statement correction', 'system:reconciliation', $at);
            """;
        command.Parameters.AddWithValue("$evidenceId", evidenceId);
        command.Parameters.AddWithValue("$digest", new string('f', 64));
        command.Parameters.AddWithValue("$decisionId", decisionId);
        command.Parameters.AddWithValue("$replacementId", replacementId);
        command.Parameters.AddWithValue("$sourceId", sourceId);
        command.Parameters.AddWithValue("$lifecycleId", LedgerId.New().ToString());
        command.Parameters.AddWithValue("$at", At);
        await command.ExecuteNonQueryAsync();
        return decisionId;
    }

    private async Task<SqliteConnection> Open() => await new LedgerConnectionFactory(new HostArtifactProtection()).OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);

    private async Task<long> Count(string table)
    {
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task Execute(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<ProcessResult> Run(string operationId, JsonElement input, string? key)
    {
        var body = JsonSerializer.Serialize(new RequestEnvelope("1.0", new SafeActor("human", "category-allocation-test"), input, key), LedgerJsonContext.Default.RequestEnvelope);
        var arguments = OperationRegistry.Create().Find(operationId)!.CliPath.Split(' ').Skip(1).Concat(["--input", "-"]).ToArray();
        return await process.RunAsync(arguments, body, CancellationToken.None);
    }

    private static CategoryAllocationResult Allocation(ProcessResult result) => Success(result, LedgerJsonContext.Default.CategoryAllocationResult);

    private static T Success<T>(ProcessResult result, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        Assert.Equal(0, result.ExitCode);
        var envelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        return JsonSerializer.Deserialize(envelope.Result!.Value, type)!;
    }

    private static void AssertError(ProcessResult result, int exitCode, string code)
    {
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(code, JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!.Error!.Code);
    }
}
