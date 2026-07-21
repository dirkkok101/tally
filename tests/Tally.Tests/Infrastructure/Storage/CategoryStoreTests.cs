using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Tally.Contracts.Ledger.Categories;
using Tally.Domain.Ledger;
using Tally.Infrastructure.Storage;
using Tally.Infrastructure.Storage.Categories;
using Xunit;

namespace Tally.Tests.Infrastructure.Storage;

[SupportedOSPlatform("linux")]
public sealed class CategoryStoreTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-category-store-{Guid.NewGuid():N}");
    private readonly HostArtifactProtection protection = new();
    private LedgerDb database = null!;
    private CategoryStore store = null!;

    [Fact]
    public async Task Parent_changes_are_append_only_and_current_projection_changes_once()
    {
        await using var connection = await Open(); await using var transaction = connection.BeginTransaction();
        var first = await Insert(connection, transaction, "First", null, 0); var second = await Insert(connection, transaction, "Second", null, 1); var child = await Insert(connection, transaction, "Child", first.CategoryId, 2);
        await store.AppendParentAsync(connection, transaction, LedgerId.New().ToString(), child, second.CategoryId, "move", "owner", At(3), CancellationToken.None); await transaction.CommitAsync();
        var current = (await store.GetAsync(child.CategoryId, true, CancellationToken.None))!;
        Assert.Equal(second.CategoryId, current.ParentCategoryId); Assert.Equal(2, current.ParentHistory.Count); Assert.Equal(1L, await Scalar(connection, "SELECT COUNT(*) FROM category_parent_current WHERE category_id = $id;", child.CategoryId));
    }

    [Fact]
    public async Task Category_and_parent_history_reject_update_and_delete()
    {
        await using var connection = await Open(); await using var transaction = connection.BeginTransaction(); var category = await Insert(connection, transaction, "Root", null, 0); await transaction.CommitAsync();
        await Assert.ThrowsAsync<SqliteException>(() => Execute(connection, "DELETE FROM spend_category WHERE category_id = $id;", category.CategoryId));
        await Assert.ThrowsAsync<SqliteException>(() => Execute(connection, "UPDATE category_parent_event SET reason = 'changed' WHERE category_id = $id;", category.CategoryId));
        await Assert.ThrowsAsync<SqliteException>(() => Execute(connection, "DELETE FROM catalogue_lifecycle_event WHERE entity_id = $id;", category.CategoryId));
    }

    [Fact]
    public async Task Cycle_and_active_child_guards_are_enforced_by_real_sqlite()
    {
        await using var connection = await Open(); await using var transaction = connection.BeginTransaction(); var rootCategory = await Insert(connection, transaction, "Root", null, 0); var child = await Insert(connection, transaction, "Child", rootCategory.CategoryId, 1);
        Assert.True(await store.WouldCreateCycleAsync(connection, transaction, rootCategory.CategoryId, child.CategoryId, CancellationToken.None)); Assert.True(await store.HasActiveChildrenAsync(connection, transaction, rootCategory.CategoryId, CancellationToken.None)); await transaction.RollbackAsync();
    }

    public async Task InitializeAsync() { database = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None); store = new(database, new(protection)); }
    public Task DisposeAsync() { if (Directory.Exists(root)) Directory.Delete(root, true); return Task.CompletedTask; }
    private async Task<CategoryCurrentState> Insert(SqliteConnection connection, SqliteTransaction transaction, string name, string? parent, int second) { var id = LedgerId.New().ToString(); await store.InsertAsync(connection, transaction, id, LedgerId.New().ToString(), LedgerId.New().ToString(), name, parent, "owner", At(second), CancellationToken.None); return (await store.FindCurrentAsync(connection, transaction, id, CancellationToken.None))!; }
    private Task<SqliteConnection> Open() => new LedgerConnectionFactory(protection).OpenAsync(database, CompleteLedgerSchema.CurrentVersion, CancellationToken.None);
    private static async Task Execute(SqliteConnection connection, string sql, string id) { await using var command = connection.CreateCommand(); command.CommandText = sql; command.Parameters.AddWithValue("$id", id); await command.ExecuteNonQueryAsync(); }
    private static async Task<long> Scalar(SqliteConnection connection, string sql, string id) { await using var command = connection.CreateCommand(); command.CommandText = sql; command.Parameters.AddWithValue("$id", id); return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture); }
    private static string At(int second) => $"2026-07-21T00:00:{second:D2}Z";
}
