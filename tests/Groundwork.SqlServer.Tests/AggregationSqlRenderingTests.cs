using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Substrate.Relational;
using Xunit;

namespace Groundwork.SqlServer.Tests;

public sealed class AggregationSqlRenderingTests
{
    [Fact]
    public void Renderer_uses_big_count_and_portable_multi_term_output_order()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("aggregation-count-order"),
            Name = "aggregation_count_order",
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "group", Type = PortableType.String, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        var profile = new AggregationProfile
        {
            Name = "summary",
            GroupByColumns = ["group"],
            Aggregates = [new Aggregate.Count("count"), new Aggregate.Min("minimum", "group")],
            AllowedPredicates = []
        };

        var sql = RelationalAggregationRenderer.Render(new SqlServerDialect(), unit, profile,
            new AggregationQuery("summary")
            {
                OrderByTerms = [
                    new AggregationOrderTerm("count", SortDirection.Descending),
                    new AggregationOrderTerm("minimum", SortDirection.Ascending)]
            }).CommandText;

        Assert.Contains("COUNT_BIG(*) AS [count]", sql, StringComparison.Ordinal);
        Assert.Contains("[count] DESC", sql, StringComparison.Ordinal);
        Assert.Contains("COLLATE Latin1_General_100_BIN2", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Renderer_uses_the_query_guid_order_key_for_aggregation_output()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("aggregation-guid-order"),
            Name = "aggregation_guid_order",
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "group", Type = PortableType.Guid, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        var profile = new AggregationProfile
        {
            Name = "summary",
            GroupByColumns = ["group"],
            Aggregates = [new Aggregate.Count("count")],
            AllowedPredicates = []
        };

        var sql = RelationalAggregationRenderer.Render(new SqlServerDialect(), unit, profile,
            new AggregationQuery("summary")
            {
                OrderByTerms = [new AggregationOrderTerm("group", SortDirection.Ascending)]
            }).CommandText;

        Assert.Contains("CONVERT(char(36), [group]) COLLATE Latin1_General_100_BIN2 ASC", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Scoped_native_sql_artifacts_inject_scope_before_grouping_and_budget_probe()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("aggregation-scoped-artifact"),
            Name = "aggregation_scoped_artifact",
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "group", Type = PortableType.String, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        var profile = new AggregationProfile
        {
            Name = "summary",
            GroupByColumns = ["group"],
            Aggregates = [new Aggregate.Count("count")],
            AllowedPredicates = []
        };
        var scope = new ColumnRef(new TableId(unit.Name), SqlServerSchemaCoordinator.ScopeColumn, QueryType.String, isNullable: false);
        var providerPredicate = new Predicate.Equal(scope, QueryConstant.Of(scope, "tenant-a"));
        var query = new AggregationQuery("summary")
        {
            OrderByTerms = [
                new AggregationOrderTerm("count", SortDirection.Descending),
                new AggregationOrderTerm("group", SortDirection.Ascending)],
            Take = 5
        };

        var command = RelationalAggregationRenderer.RenderWithProviderPredicate(new SqlServerDialect(), unit, profile, query, providerPredicate).CommandText;
        var probe = RelationalAggregationRenderer.RenderBudgetProbeWithProviderPredicate(new SqlServerDialect(), unit, profile, query, providerPredicate).CommandText;

        Assert.StartsWith("WITH ", command, StringComparison.Ordinal);
        Assert.Contains(SqlServerSchemaCoordinator.ScopeColumn, command, StringComparison.Ordinal);
        Assert.Contains("COUNT_BIG(*) AS [count]", command, StringComparison.Ordinal);
        Assert.Contains("GROUP BY", command, StringComparison.Ordinal);
        Assert.Contains("FETCH NEXT 5 ROWS ONLY", command, StringComparison.Ordinal);
        Assert.Contains(SqlServerSchemaCoordinator.ScopeColumn, probe, StringComparison.Ordinal);
        Assert.Contains("GROUP BY", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void Renderer_uses_typed_literals_and_null_aware_membership()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("aggregation-typed-predicates"),
            Name = "aggregation_typed_predicates",
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "group", Type = PortableType.String },
                new() { Name = "flag", Type = PortableType.Boolean },
                new() { Name = "moment", Type = PortableType.DateTimeOffset },
                new() { Name = "identifier", Type = PortableType.Guid },
                new() { Name = "payload", Type = PortableType.Binary },
                new() { Name = "order", Type = PortableType.Int64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        var profile = new AggregationProfile
        {
            Name = "summary",
            GroupByColumns = ["group"],
            Aggregates =
            [
                new Aggregate.FirstBy("firstFlag", "flag", "order"),
                new Aggregate.FirstBy("firstMoment", "moment", "order"),
                new Aggregate.FirstBy("firstIdentifier", "identifier", "order"),
                new Aggregate.FirstBy("firstPayload", "payload", "order")
            ],
            AllowedPredicates = new[] { "firstFlag", "firstMoment", "firstIdentifier", "firstPayload" }.Select(alias => new AggregationPredicateAllowance
                {
                    Alias = alias,
                    SupportedPredicates = new HashSet<AggregationPredicateOperator>
                    {
                        AggregationPredicateOperator.Equal,
                        AggregationPredicateOperator.In
                    }
                }).ToArray()
        };
        var instant = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var identifier = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

        var nullSql = RelationalAggregationRenderer.Render(new SqlServerDialect(), unit, profile,
            new AggregationQuery("summary")
            {
                PostPredicate = new AggregationPredicate.Comparison(
                    "firstFlag", AggregationPredicateOperator.Equal, [(object?)null])
            }).CommandText;
        var inSql = RelationalAggregationRenderer.Render(new SqlServerDialect(), unit, profile,
            new AggregationQuery("summary")
            {
                PostPredicate = new AggregationPredicate.Comparison(
                    "firstFlag", AggregationPredicateOperator.In, [(object?)null, true])
            }).CommandText;
        var typedSql = RelationalAggregationRenderer.Render(new SqlServerDialect(), unit, profile,
            new AggregationPredicateQuery("firstMoment", instant).Query).CommandText;
        var guidSql = RelationalAggregationRenderer.Render(new SqlServerDialect(), unit, profile,
            new AggregationPredicateQuery("firstIdentifier", identifier).Query).CommandText;
        var binarySql = RelationalAggregationRenderer.Render(new SqlServerDialect(), unit, profile,
            new AggregationPredicateQuery("firstPayload", new byte[] { 1, 2 }).Query).CommandText;

        Assert.Contains("[firstFlag] IS NULL", nullSql, StringComparison.Ordinal);
        Assert.Contains("([firstFlag] IN (1) OR [firstFlag] IS NULL)", inSql, StringComparison.Ordinal);
        Assert.Contains("'2024-01-02T03:04:05.0000000+00:00'", typedSql, StringComparison.Ordinal);
        Assert.Contains("CAST('00112233-4455-6677-8899-aabbccddeeff' AS uniqueidentifier)", guidSql, StringComparison.Ordinal);
        Assert.Contains("0x0102", binarySql, StringComparison.Ordinal);
    }

    private sealed class AggregationPredicateQuery
    {
        internal AggregationPredicateQuery(string alias, object value)
        {
            Query = new AggregationQuery("summary")
            {
                PostPredicate = new AggregationPredicate.Comparison(
                    alias, AggregationPredicateOperator.Equal, [value])
            };
        }

        internal AggregationQuery Query { get; }
    }

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
                new() { Name = "flag", Type = PortableType.Boolean },
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
                new Aggregate.FirstBy("firstFlag", "flag", "lowOrder"),
                new Aggregate.FirstBy("firstLow", "label", "lowOrder"),
                new Aggregate.FirstBy("firstHigh", "label", "highOrder", SortDirection.Descending)
            ],
            AllowedPredicates =
            [
                new AggregationPredicateAllowance
                {
                    Alias = "labels",
                    SupportedPredicates = new HashSet<AggregationPredicateOperator>
                    {
                        AggregationPredicateOperator.Contains
                    }
                }
            ],
            MaxInputRows = 7,
            MaxGroups = 3
        };

        var sql = RelationalAggregationRenderer.Render(new SqlServerDialect(), unit, profile).CommandText;
        var probe = RelationalAggregationRenderer.RenderBudgetProbe(new SqlServerDialect(), unit, profile).CommandText;

        Assert.Contains("TOP (8)", sql, StringComparison.Ordinal);
        Assert.Contains("__groundwork_aggregation_first_rank_firstLow", sql, StringComparison.Ordinal);
        Assert.Contains("SELECT TOP (1) first_input.[flag]", sql, StringComparison.Ordinal);
        Assert.Contains("__groundwork_aggregation_first_rank_firstHigh", sql, StringComparison.Ordinal);
        Assert.Contains("SUM(CAST([amount] AS bigint))", sql, StringComparison.Ordinal);
        Assert.Contains("STRING_ESCAPE", sql, StringComparison.Ordinal);
        Assert.Contains("CAST([label] AS nvarchar(max))", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("NCHAR(31)", sql, StringComparison.Ordinal);
        Assert.Contains("TOP (4)", probe, StringComparison.Ordinal);
        Assert.Contains("COUNT(DISTINCT [label] COLLATE Latin1_General_100_BIN2)", probe, StringComparison.Ordinal);

        var predicateSql = RelationalAggregationRenderer.Render(
            new SqlServerDialect(),
            unit,
            profile,
            new AggregationQuery("summary")
            {
                PostPredicate = new AggregationPredicate.Comparison(
                    "labels", AggregationPredicateOperator.Contains, ["plain"])
            }).CommandText;
        Assert.Contains("OPENJSON([labels])", predicateSql, StringComparison.Ordinal);
        Assert.DoesNotContain("INSTR(", predicateSql, StringComparison.Ordinal);

        var lowOrder = new ColumnRef(new TableId(unit.Name), "lowOrder", QueryType.Int64, isNullable: false);
        var sourcePredicateCommand = RelationalAggregationRenderer.Render(
            new SqlServerDialect(),
            unit,
            profile,
            new AggregationQuery("summary")
            {
                SourcePredicate = new Predicate.Equal(lowOrder, QueryConstant.Of(lowOrder, 2L))
            });
        var sourceProbeCommand = RelationalAggregationRenderer.RenderBudgetProbe(
            new SqlServerDialect(),
            unit,
            profile,
            new AggregationQuery("summary")
            {
                SourcePredicate = new Predicate.Equal(lowOrder, QueryConstant.Of(lowOrder, 2L))
            });
        Assert.Contains("WHERE ([lowOrder] IS NOT NULL AND [lowOrder] = @p0)", sourcePredicateCommand.CommandText, StringComparison.Ordinal);
        Assert.Contains("WHERE ([lowOrder] IS NOT NULL AND [lowOrder] = @p0)", sourceProbeCommand.CommandText, StringComparison.Ordinal);
        var sourceParameter = Assert.Single(sourcePredicateCommand.Parameters);
        var probeParameter = Assert.Single(sourceProbeCommand.Parameters);
        Assert.Equal(sourceParameter.Type, probeParameter.Type);
        Assert.Equal(sourceParameter.Value, probeParameter.Value);

        var label = new ColumnRef(new TableId(unit.Name), "label", QueryType.String);
        var substringCommand = RelationalAggregationRenderer.Render(
            new SqlServerDialect(),
            unit,
            profile,
            new AggregationQuery("summary")
            {
                SourcePredicate = new Predicate.Substring(label, "plain", Anchor.Contains)
            });
        Assert.Contains("CHARINDEX(@p0, [label] COLLATE Latin1_General_100_BIN2) > 0", substringCommand.CommandText, StringComparison.Ordinal);
        Assert.Equal("plain", substringCommand.Parameters.Single().Value);
    }
}
