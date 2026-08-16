using Groundwork.Kernel;
using Groundwork.Testing;
using Groundwork.Store;

namespace Groundwork.Testing.SelfTests;

internal static class TestingFixture
{
    public static StorageUnit GlobalUnit(string id = "testing-global") => new()
    {
        Id = new StorageUnitId(id),
        Name = "TestingGlobal",
        Columns = Columns(),
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes =
        [
            new IndexDefinition
            {
                Name = "by-value",
                Columns = [new IndexColumn("value")]
            },
            new IndexDefinition
            {
                Name = "unique-value",
                Columns = [new IndexColumn("uniqueValue")],
                IsUnique = true,
                MissingValues = MissingValueBehavior.Excluded
            }
        ]
    };

    public static StorageUnit ScopedUnit(string id = "testing-scoped") => new()
    {
        Id = new StorageUnitId(id),
        Name = "TestingScoped",
        Columns = Columns(),
        Key = new KeyDefinition { Columns = ["id"] },
        Scope = ScopePolicy.Scoped,
        Concurrency = ConcurrencyDeclaration.Optimistic()
    };

    public static StorageValues Values(string id, string value, string? uniqueValue = null) =>
        new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["value"] = value,
            ["uniqueValue"] = uniqueValue ?? id
        });

    public static StorageKey Key(string id) =>
        new(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = id });

    private static IReadOnlyList<ColumnDefinition> Columns() =>
    [
        new() { Name = "id", Type = PortableType.String, MaxLength = 128, IsNullable = false },
        new() { Name = "value", Type = PortableType.String, MaxLength = 128 },
        new() { Name = "uniqueValue", Type = PortableType.String, MaxLength = 128 }
    ];
}
