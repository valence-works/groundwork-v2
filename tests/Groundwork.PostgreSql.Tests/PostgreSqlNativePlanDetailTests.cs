using Groundwork.Kernel;
using Groundwork.Query.Model;
using Xunit;

namespace Groundwork.PostgreSql.Tests;

/// <summary>#421: observed sort keys on estimated PostgreSQL plans; bounds and spills stay unobserved.</summary>
public sealed class PostgreSqlNativePlanDetailTests
{
    [Fact]
    public void Sort_keys_map_to_logical_columns_with_direction_and_null_placement()
    {
        var forest = Map(
            """
            [{"Plan":{"Node Type":"Limit","Plans":[{"Node Type":"Sort","Parent Relationship":"Outer","Sort Key":["records.\"lastSeen\" DESC NULLS LAST","records.id"],"Plans":[{"Node Type":"Seq Scan","Parent Relationship":"Outer","Relation Name":"records","Schema":"public"}]}]}}]
            """,
            new Dictionary<string, string>(StringComparer.Ordinal));

        var sort = Assert.Single(Assert.IsType<ProviderPlanForest>(forest).Nodes, node => node.Operation == ProviderPlanOperator.Sort);
        var keys = Assert.IsType<ProviderPlanNodeDetails>(sort.Details).NativeSortKeys;
        Assert.NotNull(keys);
        Assert.Collection(keys.Value,
            term =>
            {
                Assert.Equal("lastSeen", term.LogicalColumn);
                Assert.Equal(OrderDirection.Descending, term.Direction);
                Assert.Equal(NullOrder.Last, term.NullPlacement);
            },
            term =>
            {
                Assert.Equal("id", term.LogicalColumn);
                Assert.Equal(OrderDirection.Ascending, term.Direction);
                Assert.Null(term.NullPlacement);
            });
        Assert.Equal(ProviderNativeBoundKind.Unknown, sort.Details!.NativeLimit.Kind);
        Assert.Null(sort.Details.Spill);
        var limit = Assert.Single(forest.Nodes, node => node.Operation == ProviderPlanOperator.Limit);
        Assert.Null(limit.Details);
    }

    [Fact]
    public void Provider_owned_sort_columns_resolve_only_through_the_recorded_mapping()
    {
        var mapped = Map(SortPlan("records.__groundwork_search_name"), new Dictionary<string, string>(StringComparer.Ordinal) { ["__groundwork_search_name"] = "name" });
        var term = Assert.Single(Assert.IsType<ProviderPlanForest>(mapped).Nodes.Single(node => node.Operation == ProviderPlanOperator.Sort).Details!.NativeSortKeys!.Value);
        Assert.Equal("name", term.LogicalColumn);

        var unmapped = Map(SortPlan("records.__groundwork_search_name"), new Dictionary<string, string>(StringComparer.Ordinal));
        Assert.Null(Assert.IsType<ProviderPlanForest>(unmapped).Nodes.Single(node => node.Operation == ProviderPlanOperator.Sort).Details);
    }

    [Theory]
    [InlineData("lower(records.name)")]
    [InlineData("(records.name COLLATE \\\"C\\\")")]
    [InlineData("other.name")]
    [InlineData("records.name DESC NULLS MAYBE")]
    public void Unsupported_sort_key_forms_leave_the_keys_unobserved_without_failing_the_plan(string key)
    {
        var forest = Map(SortPlan(key), new Dictionary<string, string>(StringComparer.Ordinal));

        var sort = Assert.Single(Assert.IsType<ProviderPlanForest>(forest).Nodes, node => node.Operation == ProviderPlanOperator.Sort);
        Assert.Null(sort.Details);
    }

    [Fact]
    public void Null_rank_and_ordinal_subplan_keys_map_to_their_columns_with_transforms()
    {
        var forest = Map(
            SubplanPlan("\"CASE WHEN (records.\\\"lastSeen\\\" IS NULL) THEN 1 ELSE 0 END\",\"records.\\\"lastSeen\\\" DESC\",\"COALESCE((SubPlan 1), ''::text)\"", "records"),
            new Dictionary<string, string>(StringComparer.Ordinal));

        var sort = Assert.Single(Assert.IsType<ProviderPlanForest>(forest).Nodes, node => node.Operation == ProviderPlanOperator.Sort);
        Assert.Collection(sort.Details!.NativeSortKeys!.Value,
            term => { Assert.Equal("lastSeen", term.LogicalColumn); Assert.Equal(ProviderOrderingTransform.NullRank, Assert.Single(term.Transforms)); Assert.Equal(OrderDirection.Ascending, term.Direction); },
            term => { Assert.Equal("lastSeen", term.LogicalColumn); Assert.Empty(term.Transforms); Assert.Equal(OrderDirection.Descending, term.Direction); },
            term => { Assert.Equal("id", term.LogicalColumn); Assert.Equal(ProviderOrderingTransform.OrdinalStringKey, Assert.Single(term.Transforms)); Assert.Equal(ProviderPredicateComparison.Ordinal, term.Comparison); });
    }

    [Fact]
    public void Ordinal_subplan_over_a_foreign_relation_leaves_the_keys_unobserved()
    {
        var forest = Map(
            SubplanPlan("\"COALESCE((SubPlan 1), ''::text)\"", "other"),
            new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Null(Assert.Single(Assert.IsType<ProviderPlanForest>(forest).Nodes, node => node.Operation == ProviderPlanOperator.Sort).Details);
    }

    private static string SubplanPlan(string keys, string callRelation) =>
        "[{\"Plan\":{\"Node Type\":\"Sort\",\"Sort Key\":[" + keys + "],\"Plans\":[{\"Node Type\":\"Seq Scan\",\"Parent Relationship\":\"Outer\",\"Relation Name\":\"records\",\"Schema\":\"public\",\"Plans\":[{\"Node Type\":\"Aggregate\",\"Parent Relationship\":\"SubPlan\",\"Subplan Name\":\"SubPlan 1\",\"Strategy\":\"Plain\",\"Plans\":[{\"Node Type\":\"Function Scan\",\"Parent Relationship\":\"Outer\",\"Function Name\":\"unnest\",\"Alias\":\"chars\",\"Function Call\":\"unnest(string_to_array((" + callRelation + ".id)::text, NULL::text))\"}]}]}]}}]";

    private static string SortPlan(string key) =>
        "[{\"Plan\":{\"Node Type\":\"Sort\",\"Sort Key\":[\"" + key + "\"],\"Plans\":[{\"Node Type\":\"Seq Scan\",\"Parent Relationship\":\"Outer\",\"Relation Name\":\"records\",\"Schema\":\"public\"}]}}]";

    private static ProviderPlanForest? Map(string rawPlan, IReadOnlyDictionary<string, string> logicalColumnsByPhysical) =>
        PostgreSqlNativePlanMapper.Map(
            rawPlan,
            "records",
            "public",
            new ProviderOpaqueIdentity(Guid.NewGuid()),
            _ => new ProviderOpaqueIdentity(Guid.NewGuid()),
            new Dictionary<string, string>(StringComparer.Ordinal),
            null,
            logicalColumnsByPhysical);
}
