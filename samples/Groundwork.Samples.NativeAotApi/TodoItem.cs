using Groundwork.Records;
using Groundwork.Schema;

namespace Groundwork.Samples.NativeAotApi;

[GwTable("todo_items")]
[GwIndex("by_done", "is_done ASC, id ASC")]
internal sealed record TodoItem(
    [property: GwKey, GwColumn(Name = "id", Length = 64, Required = true)] string Id,
    [property: GwColumn(Name = "title", Length = 200, Required = true)] string Title,
    [property: GwColumn(Name = "is_done", Required = true)] bool IsDone);

internal static class TodoStorage
{
    internal static RecordTable<TodoItem> Table { get; } =
        RecordTable.FromGenerated<TodoItem>(TodoItemStorageUnit.Definition);
}
