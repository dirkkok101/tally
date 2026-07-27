using System.Runtime.Versioning;
using System.Text.Json;
using Tally.Bootstrap;
using Tally.Cli;
using Tally.Contracts.Common;
using Tally.Contracts.Ledger.Categories;
using Tally.Domain.Ledger;
using Tally.Infrastructure.Storage;
using Xunit;

namespace Tally.Tests.Budget.LedgerContract;

/// <summary>
/// TC-BUDGET-CATEGORY-LIFECYCLE-CONTRACT / FR-BUDGET-CATEGORY-LIFECYCLE
/// Proves released ledger.category.list/get evidence BUDGET may consume.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LedgerBudgetCategoryLifecycleTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"tally-budget-cat-{Guid.NewGuid():N}");
    private TallyProcess process = null!;
    private int keySeq;

    public async Task InitializeAsync()
    {
        var db = await LedgerRuntimeBootstrap.InitializeCurrentAsync(root, CancellationToken.None);
        process = new TallyProcess(OperationRegistry.Create(), LedgerServices.Create(db));
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public void Registry_exposes_category_list_and_get_with_compatibility_range()
    {
        var registry = OperationRegistry.Create();
        var list = registry.Find("ledger.category.list")!;
        var get = registry.Find("ledger.category.get")!;
        Assert.Equal("1.0", list.MinimumContractVersion);
        Assert.Equal("1.0", list.MaximumContractVersion);
        Assert.Equal("1.0", get.MinimumContractVersion);
        Assert.Equal(typeof(CategoryListResult), list.ResultTypeInfo.Type);
        Assert.Equal(typeof(CategoryDetail), get.ResultTypeInfo.Type);
    }

    [Fact]
    public async Task List_and_get_expose_stable_id_display_name_and_active_lifecycle()
    {
        var created = await Create("Groceries");
        var listed = await List(new ListCategoriesInput());
        var item = Assert.Single(listed.Items, x => x.CategoryId == created.CategoryId);
        Assert.Equal("Groceries", item.Name);
        Assert.Equal(CategoryStatus.Active, item.Status);
        Assert.Equal(CategoryContractVersions.Current, item.LedgerContractVersion);
        Assert.Equal(CategoryContractVersions.Current, listed.LedgerContractVersion);

        var got = await Get(created.CategoryId);
        Assert.Equal(created.CategoryId, got.CategoryId);
        Assert.Equal("Groceries", got.Name);
        Assert.Equal(CategoryStatus.Active, got.Status);
        Assert.Equal(CategoryContractVersions.Current, got.LedgerContractVersion);
    }

    [Fact]
    public async Task Rename_preserves_stable_id_and_updates_display_name_only()
    {
        var created = await Create("Food");
        var renamed = await Rename(created.CategoryId, "Household");
        Assert.Equal(created.CategoryId, renamed.Category.CategoryId);
        Assert.Equal("Household", renamed.Category.Name);
        Assert.Equal(CategoryStatus.Active, renamed.Category.Status);

        var got = await Get(created.CategoryId);
        Assert.Equal(created.CategoryId, got.CategoryId);
        Assert.Equal("Household", got.Name);
    }

    [Fact]
    public async Task Archive_marks_lifecycle_archived_while_identity_remains_readable()
    {
        var created = await Create("Travel");
        var archived = await Archive(created.CategoryId);
        Assert.Equal(CategoryStatus.Archived, archived.Category.Status);
        Assert.Equal(created.CategoryId, archived.Category.CategoryId);

        var got = await Get(created.CategoryId);
        Assert.Equal(CategoryStatus.Archived, got.Status);
        Assert.Equal("Travel", got.Name);
    }

    [Fact]
    public async Task Reactivate_restores_active_lifecycle_on_same_stable_id()
    {
        var created = await Create("Utilities");
        await Archive(created.CategoryId);
        var active = await Reactivate(created.CategoryId);
        Assert.Equal(created.CategoryId, active.Category.CategoryId);
        Assert.Equal(CategoryStatus.Active, active.Category.Status);
    }

    [Fact]
    public async Task List_active_filter_excludes_archived_categories()
    {
        var keep = await Create("Keep");
        var drop = await Create("Drop");
        await Archive(drop.CategoryId);

        var active = await List(new ListCategoriesInput(Status: CategoryStatus.Active));
        Assert.Contains(active.Items, x => x.CategoryId == keep.CategoryId);
        Assert.DoesNotContain(active.Items, x => x.CategoryId == drop.CategoryId);
    }

    [Fact]
    public async Task List_archived_filter_returns_only_archived_lifecycle()
    {
        var drop = await Create("ArchivedOnly");
        await Archive(drop.CategoryId);
        var archived = await List(new ListCategoriesInput(Status: CategoryStatus.Archived));
        Assert.All(archived.Items, x => Assert.Equal(CategoryStatus.Archived, x.Status));
        Assert.Contains(archived.Items, x => x.CategoryId == drop.CategoryId);
    }

    [Fact]
    public async Task List_order_is_deterministic_across_invocations()
    {
        await Create("Zulu");
        await Create("Alpha");
        await Create("Mike");
        var first = await List(new ListCategoriesInput());
        var second = await List(new ListCategoriesInput());
        Assert.Equal(
            first.Items.Select(x => x.CategoryId),
            second.Items.Select(x => x.CategoryId));
    }

    [Fact]
    public async Task Unknown_category_get_is_stable_not_found()
    {
        var unknown = LedgerId.New().ToString();
        var result = await Run("ledger.category.get", new GetCategoryInput(unknown), null);
        Assert.Equal(4, result.ExitCode);
        Assert.Equal("LEDGER-CATEGORY-NOT-FOUND", ErrorCode(result));
    }

    [Fact]
    public async Task Display_name_is_not_an_identity_key_across_siblings()
    {
        var parentA = await Create("ParentA");
        var parentB = await Create("ParentB");
        var a = await Create("SharedName", parentA.CategoryId);
        var b = await Create("SharedName", parentB.CategoryId);
        Assert.NotEqual(a.CategoryId, b.CategoryId);
        Assert.Equal("SharedName", a.Name);
        Assert.Equal("SharedName", b.Name);
    }

    [Fact]
    public async Task Archived_then_recreate_sibling_name_gets_new_stable_id()
    {
        var first = await Create("Reusable");
        await Archive(first.CategoryId);
        var second = await Create("Reusable");
        Assert.NotEqual(first.CategoryId, second.CategoryId);
        Assert.Equal(CategoryStatus.Active, second.Status);
        Assert.Equal(CategoryStatus.Archived, (await Get(first.CategoryId)).Status);
    }

    [Fact]
    public async Task Compatibility_version_is_current_on_every_list_item()
    {
        await Create("One");
        await Create("Two");
        var listed = await List(new ListCategoriesInput());
        Assert.Equal(CategoryContractVersions.Current, listed.LedgerContractVersion);
        Assert.All(listed.Items, item => Assert.Equal(CategoryContractVersions.Current, item.LedgerContractVersion));
    }

    private async Task<CategoryDetail> Create(string name, string? parent = null) =>
        Detail(await Run("ledger.category.create", new CreateCategoryInput(name, parent), NextKey()));

    private async Task<CategoryDetail> Get(string id) =>
        Detail(await Run("ledger.category.get", new GetCategoryInput(id), null));

    private async Task<CategoryListResult> List(ListCategoriesInput input) =>
        Success(await Run("ledger.category.list", input, null), LedgerJsonContext.Default.CategoryListResult);

    private async Task<CategoryLifecycleResult> Rename(string id, string name) =>
        Success(await Run("ledger.category.rename", new RenameCategoryInput(id, name, "budget-test"), NextKey()), LedgerJsonContext.Default.CategoryLifecycleResult);

    private async Task<CategoryLifecycleResult> Archive(string id) =>
        Success(await Run("ledger.category.archive", new ArchiveCategoryInput(id, "budget-test"), NextKey()), LedgerJsonContext.Default.CategoryLifecycleResult);

    private async Task<CategoryLifecycleResult> Reactivate(string id) =>
        Success(await Run("ledger.category.reactivate", new ReactivateCategoryInput(id, "budget-test"), NextKey()), LedgerJsonContext.Default.CategoryLifecycleResult);

    private async Task<ProcessResult> Run<T>(string operationId, T input, string? key)
    {
        var type = operationId switch
        {
            "ledger.category.create" => (System.Text.Json.Serialization.Metadata.JsonTypeInfo)LedgerJsonContext.Default.CreateCategoryInput,
            "ledger.category.get" => LedgerJsonContext.Default.GetCategoryInput,
            "ledger.category.list" => LedgerJsonContext.Default.ListCategoriesInput,
            "ledger.category.rename" => LedgerJsonContext.Default.RenameCategoryInput,
            "ledger.category.archive" => LedgerJsonContext.Default.ArchiveCategoryInput,
            "ledger.category.reactivate" => LedgerJsonContext.Default.ReactivateCategoryInput,
            _ => throw new InvalidOperationException(operationId)
        };
        var element = JsonSerializer.SerializeToElement(input!, type);
        var body = JsonSerializer.Serialize(new RequestEnvelope("1.0", new("human", "budget-cat"), element, key), LedgerJsonContext.Default.RequestEnvelope);
        var args = OperationRegistry.Create().Find(operationId)!.CliPath.Split(' ').Skip(1).Concat(["--input", "-"]).ToArray();
        return await process.RunAsync(args, body, CancellationToken.None);
    }

    private string NextKey() => $"budget-cat-{Interlocked.Increment(ref keySeq)}";

    private static CategoryDetail Detail(ProcessResult result) => Success(result, LedgerJsonContext.Default.CategoryDetail);

    private static T Success<T>(ProcessResult result, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        Assert.Equal(0, result.ExitCode);
        var envelope = JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!;
        return JsonSerializer.Deserialize(envelope.Result!.Value, type)!;
    }

    private static string ErrorCode(ProcessResult result) =>
        JsonSerializer.Deserialize(result.Stdout, LedgerJsonContext.Default.ResultEnvelope)!.Error!.Code;
}
