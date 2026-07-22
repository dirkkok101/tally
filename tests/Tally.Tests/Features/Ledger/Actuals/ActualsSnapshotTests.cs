using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tally.Application;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Accounts;
using Tally.Contracts.Ledger.Actuals;
using Tally.Contracts.Ledger.Categories;
using Tally.Contracts.Ledger.Dimensions;
using Tally.Contracts.Ledger.Evidence;
using Tally.Contracts.Ledger.Transactions;
using Tally.Domain.Ledger;
using Tally.Features.Ledger.Actuals;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Actuals;
using Xunit;

namespace Tally.Tests.Features.Ledger.Actuals;

[SupportedOSPlatform("linux")]
public sealed class ActualsSnapshotTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tally-actuals-snapshot-" + Guid.NewGuid().ToString("N"));
    private readonly LedgerConnectionFactory factory = new(new HostArtifactProtection());
    private LedgerDb database = null!;
    private TallyProcess process = null!;
    private ActualsQueryHandler handler = null!;
    private string accountId = null!;
    private string foodId = null!;
    private string groceriesId = null!;
    private string travelId = null!;
    private string personalPoolId = null!;
    private string companyPoolId = null!;
    private string purchaseId = null!;
    private string uncategorizedId = null!;
    private string incomeId = null!;

    [Fact]
    public async Task First_page_materializes_full_exact_totals_groups_and_frozen_items()
    {
        var result = await Query(new ActualsFilterInput(GroupBy: ActualsGrouping.PoolCategory), 1);

        Assert.Equal(3, result.TotalCount);
        Assert.Single(result.Items);
        Assert.NotNull(result.Cursor);
        AssertTotals(result.Totals, "38", "12", "12");
        Assert.Equal(2, result.Groups.Count);
        Assert.All(result.Groups, group => Assert.Equal(ActualsGrouping.PoolCategory, group.Kind));
        Assert.True(DateTimeOffset.Parse(result.ExpiresAt, System.Globalization.CultureInfo.InvariantCulture) > DateTimeOffset.UtcNow);
        Assert.Equal(1, await Count("query_snapshot"));
        Assert.Equal(3, await Count("query_snapshot_item"));
    }

    [Fact]
    public async Task Every_page_returns_each_transaction_once_with_unchanged_full_set_totals()
    {
        var pages = await AllPages(new ActualsFilterInput(GroupBy: ActualsGrouping.CategoryDirect), 1);

        Assert.Equal(3, pages.Count);
        Assert.Equal(3, pages.SelectMany(page => page.Items).Select(item => item.TransactionId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(pages, page => AssertTotals(page.Totals, "38", "12", "12"));
        Assert.All(pages, page => Assert.Equal(pages[0].Groups, page.Groups));
        Assert.Null(pages[^1].Cursor);
    }

    [Fact]
    public async Task Later_pages_ignore_transactions_recorded_after_the_snapshot_commit()
    {
        var first = await Query(new(), 1);
        var added = await Record("-99", "2026-07-31", "late mutation");

        var later = await Continue(first.Cursor!);
        var allSnapshotIds = first.Items.Concat(later.Items).Select(item => item.TransactionId).ToArray();

        Assert.DoesNotContain(added.TransactionId, allSnapshotIds);
        Assert.Equal(first.TotalCount, later.TotalCount);
        Assert.Equal(first.Totals, later.Totals);
    }

    [Fact]
    public async Task Category_reparenting_changes_new_queries_but_not_frozen_ancestry_or_groups()
    {
        var first = await Query(new ActualsFilterInput(GroupBy: ActualsGrouping.CategorySubtree), 1);
        await Reparent(groceriesId, travelId);

        var pages = await RemainingPages(first);
        var frozen = pages.SelectMany(page => page.Items).Single(item => item.TransactionId == purchaseId);
        var current = await Query(new ActualsFilterInput(CategoryIds: [travelId], CategoryScope: ActualsCategorySelectionScope.Subtree, GroupBy: ActualsGrouping.CategorySubtree), 10);

        Assert.Equal([foodId, groceriesId], frozen.FrozenAncestryIds);
        Assert.Equal([travelId, groceriesId], current.Items.Single(item => item.TransactionId == purchaseId).FrozenAncestryIds);
        Assert.All(pages, page => Assert.Equal(first.Groups, page.Groups));
    }

    [Fact]
    public async Task Pool_correction_after_commit_does_not_change_later_snapshot_pages()
    {
        var first = await Query(new ActualsFilterInput(GroupBy: ActualsGrouping.Pool), 1);
        await CorrectPool(purchaseId, companyPoolId);

        var pages = await RemainingPages(first);
        var frozen = pages.SelectMany(page => page.Items).Single(item => item.TransactionId == purchaseId);
        var current = await Query(new ActualsFilterInput(PoolIds: [companyPoolId]), 10);

        Assert.Equal(personalPoolId, frozen.PoolId);
        Assert.Contains(current.Items, item => item.TransactionId == purchaseId && item.PoolId == companyPoolId);
        Assert.All(pages, page => Assert.Equal(first.Groups, page.Groups));
    }

    [Fact]
    public async Task Competing_writer_returns_stable_busy_without_a_partial_snapshot()
    {
        await using var lockConnection = await Open();
        await using var writeLock = lockConnection.BeginTransaction(deferred: false);

        var result = await handler.HandleAsync(new(new(), 1), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ActualsErrors.SnapshotBusy, result.ErrorCode);
        Assert.Equal(0, await Count("query_snapshot", lockConnection, writeLock));
        await writeLock.RollbackAsync();
    }

    [Fact]
    public async Task Failure_before_commit_rolls_back_snapshot_header_items_and_groups()
    {
        await using (var connection = await Open())
        {
            await Execute(connection, """
                CREATE TRIGGER fail_actuals_item BEFORE INSERT ON query_snapshot_item
                BEGIN SELECT RAISE(ABORT, 'injected actuals snapshot failure'); END;
                """);
        }

        await Assert.ThrowsAsync<SqliteException>(() => handler.HandleAsync(new(new(), 1), CancellationToken.None));

        Assert.Equal(0, await Count("query_snapshot"));
        Assert.Equal(0, await Count("query_snapshot_item"));
        Assert.Equal(0, await Count("query_snapshot_group"));
    }

    [Fact]
    public async Task New_snapshot_creation_opportunistically_removes_expired_ephemeral_snapshots()
    {
        await using (var connection = await Open())
        {
            await Execute(connection, """
                INSERT INTO query_snapshot VALUES ('expired', '1.0', 'filter', 'generation', 'hierarchy', 'ephemeral', '2000-01-01T00:00:00Z', '2000-01-01T00:01:00Z', 0, 0, 0);
                """);
        }

        var result = await Query(new(), 10);

        Assert.Equal(1, await Count("query_snapshot"));
        Assert.NotEqual("expired", result.SnapshotId);
    }

    [Fact]
    public async Task Missing_snapshot_fails_without_recomputing_against_live_state()
    {
        var first = await Query(new(), 1);
        await using (var connection = await Open())
        {
            await Execute(connection, "DELETE FROM query_snapshot WHERE snapshot_id = $id;", ("$id", first.SnapshotId));
        }
        await Record("-50", "2026-07-30", "must not leak into cursor");

        var result = await handler.HandleAsync(new(Cursor: first.Cursor), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ActualsErrors.SnapshotNotFound, result.ErrorCode);
    }

    [Fact]
    public async Task Later_page_reads_do_not_create_or_modify_snapshot_rows()
    {
        var first = await Query(new(), 1);
        var before = await SnapshotDigest(first.SnapshotId);

        _ = await Continue(first.Cursor!);

        Assert.Equal(before, await SnapshotDigest(first.SnapshotId));
        Assert.Equal(1, await Count("query_snapshot"));
    }

    public async Task InitializeAsync()
    {
        database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        process = new(OperationRegistry.Create(), LedgerServices.Create(database));
        handler = new(new QuerySnapshotStore(database, factory));

        accountId = (await CreateAccount()).AccountId;
        foodId = (await CreateCategory("Food")).CategoryId;
        groceriesId = (await CreateCategory("Groceries", foodId)).CategoryId;
        travelId = (await CreateCategory("Travel")).CategoryId;
        personalPoolId = (await CreatePool("Personal")).PoolId;
        companyPoolId = (await CreatePool("Company")).PoolId;

        var purchase = await Record("-10", "2026-07-01", "Groceries");
        purchaseId = purchase.TransactionId;
        await AssignCategory(purchaseId, groceriesId);
        await AssignPool(purchaseId, personalPoolId);
        uncategorizedId = (await Record("-2", "2026-07-02", "Uncategorized")).TransactionId;
        incomeId = (await Record("50", "2026-07-03", "Income")).TransactionId;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private async Task<List<ActualsQueryResult>> AllPages(ActualsFilterInput filter, int pageSize)
    {
        return await RemainingPages(await Query(filter, pageSize));
    }

    private async Task<List<ActualsQueryResult>> RemainingPages(ActualsQueryResult first)
    {
        var pages = new List<ActualsQueryResult> { first };
        while (pages[^1].Cursor is { } cursor) pages.Add(await Continue(cursor));
        return pages;
    }

    private async Task<ActualsQueryResult> Query(ActualsFilterInput filter, int pageSize) =>
        Success(await handler.HandleAsync(new(filter, pageSize), CancellationToken.None));

    private async Task<ActualsQueryResult> Continue(string cursor) =>
        Success(await handler.HandleAsync(new(Cursor: cursor), CancellationToken.None));

    private static ActualsQueryResult Success(CommandResult<JsonElement> result)
    {
        Assert.True(result.IsSuccess, result.ErrorCode);
        return JsonSerializer.Deserialize(result.Value, ActualsJsonContext.Default.ActualsQueryResult)!;
    }

    private Task<AccountDetail> CreateAccount() => RunSuccess(
        "ledger.account.create",
        new CreateAccountInput("Test Bank", "Primary", AccountType.Cheque, "****1111", "ZAR"),
        LedgerJsonContext.Default.CreateAccountInput,
        LedgerJsonContext.Default.AccountDetail,
        "account");

    private Task<CategoryDetail> CreateCategory(string name, string? parentId = null) => RunSuccess(
        "ledger.category.create",
        new CreateCategoryInput(name, parentId),
        LedgerJsonContext.Default.CreateCategoryInput,
        LedgerJsonContext.Default.CategoryDetail,
        "category-" + name);

    private Task<SpendPoolDetail> CreatePool(string name) => RunSuccess(
        "ledger.pool.create",
        new CreateSpendPoolInput(name),
        LedgerJsonContext.Default.CreateSpendPoolInput,
        LedgerJsonContext.Default.SpendPoolDetail,
        "pool-" + name);

    private Task<TransactionDetail> Record(string amount, string date, string description)
    {
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(description + date + amount)));
        return RunSuccess(
            "ledger.transaction.record",
            new RecordTransactionInput(accountId, amount, "ZAR", date, null, description, null, null, new(EvidenceKind.AgentCapture, digest, null, null, null)),
            LedgerJsonContext.Default.RecordTransactionInput,
            LedgerJsonContext.Default.TransactionDetail,
            "record-" + digest[..12]);
    }

    private Task<CategoryAllocationResult> AssignCategory(string transactionId, string categoryId) => RunSuccess(
        "ledger.transaction.category.assign",
        new AssignCategoryInput(transactionId, categoryId, "owner category"),
        LedgerJsonContext.Default.AssignCategoryInput,
        LedgerJsonContext.Default.CategoryAllocationResult,
        "category-assign-" + transactionId);

    private async Task<PoolAssignmentResult> AssignPool(string transactionId, string poolId)
    {
        var current = (await GetTransaction(transactionId)).Pool.PoolAssignmentEventId;
        return await RunSuccess(
            "ledger.transaction.pool.assign",
            new AssignPoolInput(transactionId, current, new(TransactionPoolState.Assigned, poolId), "owner pool"),
            LedgerJsonContext.Default.AssignPoolInput,
            LedgerJsonContext.Default.PoolAssignmentResult,
            "pool-assign-" + transactionId);
    }

    private async Task CorrectPool(string transactionId, string poolId)
    {
        var current = (await GetTransaction(transactionId)).Pool.PoolAssignmentEventId;
        _ = await RunSuccess(
            "ledger.transaction.pool.correct",
            new CorrectPoolInput(transactionId, current, new(TransactionPoolState.Assigned, poolId), "owner correction"),
            LedgerJsonContext.Default.CorrectPoolInput,
            LedgerJsonContext.Default.PoolAssignmentResult,
            "pool-correct-" + transactionId);
    }

    private async Task Reparent(string categoryId, string parentId)
    {
        _ = await RunSuccess(
            "ledger.category.reparent",
            new ReparentCategoryInput(categoryId, parentId, "owner move"),
            LedgerJsonContext.Default.ReparentCategoryInput,
            LedgerJsonContext.Default.CategoryReparentResult,
            "reparent-" + categoryId);
    }

    private Task<TransactionDetail> GetTransaction(string transactionId) => RunSuccess(
        "ledger.transaction.get",
        new GetTransactionInput(transactionId),
        LedgerJsonContext.Default.GetTransactionInput,
        LedgerJsonContext.Default.TransactionDetail,
        null);

    private async Task<TResult> RunSuccess<TInput, TResult>(
        string operationId,
        TInput input,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TInput> inputType,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultType,
        string? key)
    {
        var envelope = new RequestEnvelope("1.0", new("human", "actuals-test"), JsonSerializer.SerializeToElement(input, inputType), key);
        var body = JsonSerializer.Serialize(envelope, LedgerJsonContext.Default.RequestEnvelope);
        var arguments = OperationRegistry.Create().Find(operationId)!.CliPath.Split(' ').Skip(1).Concat(["--input", "-"]).ToArray();
        var result = await process.RunAsync(arguments, body, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        var response = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        return JsonSerializer.Deserialize(response.Result!.Value, resultType)!;
    }

    private async Task<long> Count(string table, SqliteConnection? connection = null, SqliteTransaction? transaction = null)
    {
        var ownsConnection = connection is null;
        connection ??= await Open();
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT COUNT(*) FROM {table};";
            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            if (ownsConnection) await connection.DisposeAsync();
        }
    }

    private async Task<string> SnapshotDigest(string snapshotId)
    {
        await using var connection = await Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT snapshot.contract_version || '|' || snapshot.canonical_filter_hash || '|' || snapshot.generation_fingerprint || '|' || snapshot.category_hierarchy_fingerprint || '|' || snapshot.expires_at || '|' ||
                   (SELECT COUNT(*) FROM query_snapshot_item WHERE snapshot_id = snapshot.snapshot_id) || '|' ||
                   (SELECT COUNT(*) FROM query_snapshot_group WHERE snapshot_id = snapshot.snapshot_id)
            FROM query_snapshot AS snapshot WHERE snapshot.snapshot_id = $id;
            """;
        command.Parameters.AddWithValue("$id", snapshotId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<SqliteConnection> Open() => await factory.OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);

    private static async Task Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static void AssertTotals(ActualsTotalsResult totals, string net, string spend, string budget)
    {
        Assert.Equal(net, totals.NetAccountMovement);
        Assert.Equal(spend, totals.ExternalSpend);
        Assert.Equal(budget, totals.BudgetActual);
    }
}
