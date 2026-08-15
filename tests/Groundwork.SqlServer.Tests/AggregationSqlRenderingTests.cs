using Groundwork.Kernel;
using Groundwork.Substrate.Relational;
using Xunit;

namespace Groundwork.SqlServer.Tests;

public sealed class AggregationSqlRenderingTests
{
    [Fact]
    public void Renderer_bounds_input_and_uses_independent_FirstBy_json_sets_and_widened_sums()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("aggregation-render"),
            Name = "aggregation_render",
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, MaxLength = 32, IsNullable = false },
                new() { Name = "group", Type = PortableType.String, MaxLength = 32, IsNullable = false },
                new() { Name = "amount", Type = PortableType.Int32 },
                new() { Name = "label", Type = PortableType.String, MaxLength = 128 },
                new() { Name = "lowOrder", Type = PortableType.Int64, IsNullable = false },
                new() { Name = "highOrder", Type = PortableType.Int64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        var profile = new AggregationProfile
        {
            Name = "summary",
            GroupByColumns = ["group"],
            Aggregates =
            [
                new Aggregate.Sum("total", "amount"),
                new Aggregate.SetUnion("labels", "label", 4),
                new Aggregate.FirstBy("firstLow", "label", "lowOrder"),
                new Aggregate.FirstBy("firstHigh", "label", "highOrder", SortDirection.Descending)
            ],
            MaxInputRows = 7,
            MaxGroups = 3
        };

        var sql = RelationalAggregationRenderer.Render(new SqlServerDialect(), unit, profile).CommandText;
        var probe = RelationalAggregationRenderer.RenderBudgetProbe(new SqlServerDialect(), unit, profile).CommandText;

        Assert.Contains("TOP (8)", sql, StringComparison.Ordinal);
        Assert.Contains("__groundwork_aggregation_first_rank_firstLow", sql, StringComparison.Ordinal);
        Assert.Contains("__groundwork_aggregation_first_rank_firstHigh", sql, StringComparison.Ordinal);
        Assert.Contains("SUM(CAST([amount] AS bigint))", sql, StringComparison.Ordinal);
        Assert.Contains("STRING_ESCAPE", sql, StringComparison.Ordinal);
        Assert.Contains("CAST([label] AS nvarchar(max))", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("NCHAR(31)", sql, StringComparison.Ordinal);
        Assert.Contains("TOP (4)", probe, StringComparison.Ordinal);
        Assert.Contains("COUNT(DISTINCT [label] COLLATE Latin1_General_100_BIN2)", probe, StringComparison.Ordinal);
    }
}
