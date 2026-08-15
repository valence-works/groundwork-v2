using Groundwork.Kernel;
using Xunit;

namespace Groundwork.Kernel.Tests;

public sealed class AggregationProfileTests
{
    [Fact]
    public void Declaration_refuses_incompatible_types_and_duplicate_names()
    {
        var unit = Unit(
            new ColumnDefinition { Name = "group", Type = PortableType.String, IsNullable = false },
            new ColumnDefinition { Name = "payload", Type = PortableType.Boolean },
            new ColumnDefinition { Name = "order", Type = PortableType.Int64, IsNullable = false });
        var invalid = Profile(
            new Aggregate.Sum("group", "payload"),
            new Aggregate.Min("group", "order"));

        var exception = Assert.Throws<AggregationValidationException>(() => AggregationProfileValidator.Validate(unit, invalid));

        Assert.Contains(exception.Errors, error => error.Code == "GW-AGG-TYPE-001");
        Assert.Contains(exception.Errors, error => error.Code == "GW-AGG-DECL-007");
    }

    [Fact]
    public void Executor_uses_portable_empty_null_sum_and_set_union_semantics()
    {
        var unit = Unit(
            new ColumnDefinition { Name = "group", Type = PortableType.String, IsNullable = true },
            new ColumnDefinition { Name = "amount", Type = PortableType.Int32 },
            new ColumnDefinition { Name = "label", Type = PortableType.String },
            new ColumnDefinition { Name = "order", Type = PortableType.Int64, IsNullable = false });
        var profile = Profile(
            new Aggregate.Sum("total", "amount"),
            new Aggregate.Min("minimum", "amount"),
            new Aggregate.SetUnion("labels", "label", 3),
            new Aggregate.FirstBy("first", "label", "order"));
        var rows = new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["group"] = "a", ["amount"] = null, ["label"] = "z", ["order"] = 2L },
            new Dictionary<string, object?> { ["group"] = "a", ["amount"] = 4, ["label"] = null, ["order"] = 1L },
            new Dictionary<string, object?> { ["group"] = null, ["amount"] = null, ["label"] = null, ["order"] = 3L }
        };

        var result = AggregationExecutor.Execute(unit, profile, rows);

        var a = Assert.Single(result.Rows, row => Equals(row["group"], "a"));
        Assert.Equal(4L, Assert.IsType<long>(a["total"]));
        Assert.Equal(4, Assert.IsType<int>(a["minimum"]));
        Assert.Equal(new[] { "z" }, Assert.IsAssignableFrom<IEnumerable<string>>(a["labels"]));
        Assert.Null(a["first"]);
        var empty = Assert.Single(result.Rows, row => row["group"] is null);
        Assert.Null(empty["total"]);
        Assert.Null(empty["minimum"]);
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<string>>(empty["labels"]));
    }

    [Fact]
    public void Executor_refuses_input_group_and_set_union_overflow_instead_of_truncating()
    {
        var unit = Unit(
            new ColumnDefinition { Name = "group", Type = PortableType.String },
            new ColumnDefinition { Name = "value", Type = PortableType.String },
            new ColumnDefinition { Name = "order", Type = PortableType.Int64, IsNullable = false });
        var profile = Profile(new Aggregate.SetUnion("values", "value", 1)) with
        {
            MaxInputRows = 1,
            MaxGroups = 1
        };
        var rows = new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["group"] = "a", ["value"] = "a", ["order"] = 1L },
            new Dictionary<string, object?> { ["group"] = "a", ["value"] = "b", ["order"] = 2L }
        };

        var exception = Assert.Throws<AggregationBudgetExceededException>(() => AggregationExecutor.Execute(unit, profile, rows));

        Assert.Equal("GW-AGG-BOUND-004", exception.Code);
    }

    private static StorageUnit Unit(params ColumnDefinition[] columns) => new()
    {
        Id = new StorageUnitId("aggregation-tests"),
        Name = "aggregation_tests",
        Columns = columns,
        Key = new KeyDefinition { Columns = ["group"] }
    };

    private static AggregationProfile Profile(params Aggregate[] aggregates) => new()
    {
        Name = "summary",
        GroupByColumns = ["group"],
        Aggregates = aggregates,
        AllowedPredicates = []
    };
}
