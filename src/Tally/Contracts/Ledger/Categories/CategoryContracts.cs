using System.Text.Json.Serialization;

namespace Tally.Contracts.Ledger.Categories;

[JsonConverter(typeof(JsonStringEnumConverter<CategoryStatus>))]
public enum CategoryStatus { [JsonStringEnumMemberName("active")] Active, [JsonStringEnumMemberName("archived")] Archived }
[JsonConverter(typeof(JsonStringEnumConverter<CategoryListScope>))]
public enum CategoryListScope { [JsonStringEnumMemberName("all")] All, [JsonStringEnumMemberName("children")] Children, [JsonStringEnumMemberName("subtree")] Subtree }
[JsonConverter(typeof(JsonStringEnumConverter<CategoryLifecycleAction>))]
public enum CategoryLifecycleAction { [JsonStringEnumMemberName("create")] Create, [JsonStringEnumMemberName("rename")] Rename, [JsonStringEnumMemberName("archive")] Archive, [JsonStringEnumMemberName("reactivate")] Reactivate }
[JsonConverter(typeof(JsonStringEnumConverter<CategoryParentAction>))]
public enum CategoryParentAction { [JsonStringEnumMemberName("initialize")] Initialize, [JsonStringEnumMemberName("reparent")] Reparent }

public sealed record CreateCategoryInput([property: JsonRequired] string Name, string? ParentCategoryId = null);
public sealed record GetCategoryInput([property: JsonRequired] string CategoryId, bool IncludeHistory = false);
public sealed record ListCategoriesInput(CategoryStatus? Status = null, string? ParentCategoryId = null, CategoryListScope Scope = CategoryListScope.All);
public sealed record RenameCategoryInput([property: JsonRequired] string CategoryId, [property: JsonRequired] string NewName, [property: JsonRequired] string Reason);
public sealed record ReparentCategoryInput([property: JsonRequired] string CategoryId, string? ParentCategoryId, [property: JsonRequired] string Reason);
public sealed record ArchiveCategoryInput([property: JsonRequired] string CategoryId, [property: JsonRequired] string Reason);
public sealed record ReactivateCategoryInput([property: JsonRequired] string CategoryId, [property: JsonRequired] string Reason);

public sealed record CategoryLifecycleHistoryItem(string LifecycleEventId, CategoryLifecycleAction Action, string? PreviousName, string? NewName, string? Reason, string Actor, string OccurredAt, string? PreviousLifecycleEventId);
public sealed record CategoryParentHistoryItem(string ParentEventId, CategoryParentAction Action, string? ParentCategoryId, string Reason, string Actor, string OccurredAt, string? PreviousParentEventId);
public sealed record CategoryDetail(string CategoryId, string Name, CategoryStatus Status, string? ParentCategoryId, int Depth, IReadOnlyList<string> AncestryIds, string CreatedActor, string CreatedAt, IReadOnlyList<CategoryLifecycleHistoryItem> LifecycleHistory, IReadOnlyList<CategoryParentHistoryItem> ParentHistory);
public sealed record CategorySummary(string CategoryId, string Name, CategoryStatus Status, string? ParentCategoryId, int Depth, IReadOnlyList<string> AncestryIds);
public sealed record CategoryListResult(IReadOnlyList<CategorySummary> Items);
public sealed record CategoryLifecycleResult(CategoryDetail Category, string LifecycleEventId);
public sealed record CategoryReparentResult(CategoryDetail Category, string ParentEventId);
