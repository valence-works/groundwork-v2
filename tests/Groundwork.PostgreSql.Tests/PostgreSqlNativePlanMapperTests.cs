using Groundwork.Kernel;
using Xunit;

namespace Groundwork.PostgreSql.Tests;

public sealed class PostgreSqlNativePlanMapperTests
{
    [Theory]
    [InlineData("")]
    [InlineData(",\"Parent Relationship\":\"Inner\"")]
    [InlineData(",\"Parent Relationship\":\"Member\"")]
    public void Missing_or_incompatible_input_role_cannot_attest_to_a_complete_tree(string role)
    {
        var raw = "[{\"Plan\":{\"Node Type\":\"Limit\",\"Plans\":[{\"Node Type\":\"Seq Scan\",\"Relation Name\":\"records\",\"Schema\":\"public\"" + role + "}]}}]";
        Assert.Null(Map(raw, Identity(), _ => Identity(), new Dictionary<string, string>()));
    }

    [Theory]
    [InlineData("\"invented\"")]
    [InlineData("42")]
    public void Malformed_aggregate_strategy_withholds_the_tree(string strategy)
    {
        var raw = "[{\"Plan\":{\"Node Type\":\"Aggregate\",\"Strategy\":" + strategy + ",\"Plans\":[{\"Node Type\":\"Seq Scan\",\"Parent Relationship\":\"Outer\",\"Relation Name\":\"records\",\"Schema\":\"public\"}]}}]";
        Assert.Null(Map(raw, Identity(), _ => Identity(), new Dictionary<string, string>()));
    }

    [Fact]
    public void Native_computational_subplan_is_preserved_beneath_catalog_witnessed_index_access()
    {
        var index = Identity();
        var forest = PostgreSqlNativePlanMapper.Map(
            """
            [{"Plan":{"Node Type":"Index Scan","Relation Name":"records","Schema":"public","Index Name":"records_pkey","Index Cond":"(scope = 'redacted')","Plans":[{"Node Type":"Aggregate","Parent Relationship":"SubPlan","Strategy":"Plain","Plans":[{"Node Type":"Function Scan","Parent Relationship":"Outer"}]}]}}]
            """, "records", "public", Identity(), _ => index,
            new Dictionary<string, string>(), new HashSet<string>(StringComparer.Ordinal) { "records_pkey" });

        var nodes = Assert.IsType<ProviderPlanForest>(forest).Nodes;
        Assert.Equal(3, nodes.Length);
        Assert.Equal(index, nodes[0].IndexId);
        Assert.Null(nodes[0].LogicalIndexName);
        Assert.Equal("Aggregate", nodes[1].Operation.ToString());
        Assert.Equal(0, nodes[1].ParentId);
        Assert.Equal("FunctionScan", nodes[2].Operation.ToString());
        Assert.Equal(1, nodes[2].ParentId);
        Assert.All(nodes.Skip(1), node => Assert.Null(node.TargetId));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("[]")]
    [InlineData("\"\"")]
    public void A_malformed_index_condition_is_not_evidence_of_an_unbounded_index_scan(string condition)
    {
        var rawPlan = "[{\"Plan\":{\"Node Type\":\"Index Scan\",\"Relation Name\":\"records\",\"Schema\":\"public\",\"Index Name\":\"ix_records_value\",\"Index Cond\":" + condition + "}}]";

        Assert.Null(Map(rawPlan, Identity(), _ => Identity(), new Dictionary<string, string>
        {
            ["ix_records_value"] = "by_value"
        }));
    }

    [Theory]
    [InlineData("Seq Scan", "[{\"Node Type\":\"Limit\"}]")]
    [InlineData("Limit", "[{\"Node Type\":\"Seq Scan\",\"Relation Name\":\"records\",\"Schema\":\"public\"},{\"Node Type\":\"Materialize\"}]")]
    public void Invalid_native_child_layout_withholds_the_entire_forest(string operation, string children)
    {
        var rawPlan = "[{\"Plan\":{\"Node Type\":\"" + operation + "\",\"Relation Name\":\"records\",\"Schema\":\"public\",\"Plans\":" + children + "}}]";

        Assert.Null(Map(rawPlan, Identity(), _ => Identity(), new Dictionary<string, string>()));
    }

    [Theory]
    [InlineData("[{\"Plan\":{\"Node Type\":\"Seq Scan\",\"Relation Name\":\"records\"}}]")]
    [InlineData("[{\"Plan\":{\"Node Type\":\"Seq Scan\",\"Relation Name\":\"records\",\"Schema\":\"other\"}}]")]
    public void Homonymous_or_unwitnessed_namespace_cannot_prove_the_target(string rawPlan) =>
        Assert.Null(Map(rawPlan, Identity(), _ => Identity(), new Dictionary<string, string>()));

    [Fact]
    public void Maps_the_winning_limit_sort_index_tree_and_preserves_parentage()
    {
        var targetId = Identity();
        var indexId = Identity();
        var forest = Map(
            """
            [{"Plan":{"Node Type":"Limit","Plans":[{"Node Type":"Sort","Parent Relationship":"Outer","Sort Key":["value"],"Plans":[{"Node Type":"Index Scan","Parent Relationship":"Outer","Relation Name":"records","Schema":"public","Index Name":"ix_records_value","Index Cond":"(value = 1)"}]}]}}]
            """,
            targetId,
            _ => indexId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ix_records_value"] = "by_value"
            });

        var nodes = Assert.IsType<ProviderPlanForest>(forest).Nodes;
        Assert.Collection(nodes,
            limit =>
            {
                Assert.Equal(ProviderPlanOperator.Limit, limit.Operation);
                Assert.Null(limit.ParentId);
            },
            sort =>
            {
                Assert.Equal(ProviderPlanOperator.Sort, sort.Operation);
                Assert.Equal(0, sort.ParentId);
                Assert.Null(sort.SortPurpose);
            },
            access =>
            {
                Assert.Equal(ProviderPlanOperator.IndexSearch, access.Operation);
                Assert.Equal(1, access.ParentId);
                Assert.Equal(targetId, access.TargetId);
                Assert.Equal(indexId, access.IndexId);
                Assert.Equal("by_value", access.LogicalIndexName);
                Assert.False(access.IsCovering);
            });
    }

    [Fact]
    public void Maps_materialize_parent_and_index_only_covering_access()
    {
        var forest = Map(
            """
            [{"Plan":{"Node Type":"Materialize","Plans":[{"Node Type":"Index Only Scan","Parent Relationship":"Outer","Relation Name":"records","Schema":"public","Index Name":"ix_records_value","Index Cond":"(value = 1)"}]}}]
            """,
            Identity(),
            _ => Identity(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ix_records_value"] = "by_value"
            });

        var nodes = Assert.IsType<ProviderPlanForest>(forest).Nodes;
        Assert.Equal(ProviderPlanOperator.Materialize, nodes[0].Operation);
        Assert.Equal(ProviderPlanOperator.IndexSearch, nodes[1].Operation);
        Assert.Equal(0, nodes[1].ParentId);
        Assert.True(nodes[1].IsCovering);
    }

    [Fact]
    public void An_index_scan_without_index_condition_is_a_scan_not_a_search()
    {
        var forest = Map(
            """
            [{"Plan":{"Node Type":"Index Scan","Relation Name":"records","Schema":"public","Index Name":"ix_records_value"}}]
            """,
            Identity(),
            _ => Identity(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ix_records_value"] = "by_value"
            });

        Assert.Equal(ProviderPlanOperator.IndexScan,
            Assert.Single(Assert.IsType<ProviderPlanForest>(forest).Nodes).Operation);
    }

    [Fact]
    public void Unknown_child_operator_withholds_the_entire_forest()
    {
        var forest = Map(
            """
            [{"Plan":{"Node Type":"Limit","Plans":[{"Node Type":"Incremental Sort","Plans":[{"Node Type":"Index Scan","Relation Name":"records","Schema":"public","Index Name":"ix_records_value"}]}]}}]
            """,
            Identity(),
            _ => Identity(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ix_records_value"] = "by_value"
            });

        Assert.Null(forest);
    }

    [Fact]
    public void Access_requires_exact_target_and_declared_physical_index()
    {
        Assert.Null(Map(
            """[{"Plan":{"Node Type":"Seq Scan","Relation Name":"records_archive"}}]""",
            Identity(), _ => Identity(), new Dictionary<string, string>()));
        Assert.Null(Map(
            """[{"Plan":{"Node Type":"Index Scan","Relation Name":"records","Schema":"public","Index Name":"ix_records_value_archive"}}]""",
            Identity(), _ => Identity(), new Dictionary<string, string>
            {
                ["ix_records_value"] = "by_value"
            }));
    }

    [Fact]
    public void Chose_index_ignores_unrelated_json_and_searches_only_the_winning_plan_tree()
    {
        var physical = "ix_records_value";
        var unrelated =
            "[{\"Plan\":{\"Node Type\":\"Seq Scan\",\"Relation Name\":\"records\",\"Output\":[\"ix_records_value\"]},\"Rejected\":{\"Node Type\":\"Index Scan\",\"Index Name\":\"ix_records_value\"}}]";

        Assert.False(PostgreSqlExplainPlanInspector.ChoseIndex(unrelated, physical));
        Assert.True(PostgreSqlExplainPlanInspector.ChoseIndex(
            """[{"Plan":{"Node Type":"Limit","Plans":[{"Node Type":"Index Scan","Relation Name":"records","Schema":"public","Index Name":"ix_records_value"}]}}]""",
            physical));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[{\"Plan\":{\"Node Type\":\"Limit\",\"Plans\":{}}}]")]
    public void Malformed_plan_envelopes_are_unsupported(string rawPlan) =>
        Assert.Null(Map(rawPlan, Identity(), _ => Identity(), new Dictionary<string, string>()));

    private static ProviderPlanForest? Map(
        string rawPlan,
        ProviderOpaqueIdentity targetId,
        Func<string, ProviderOpaqueIdentity> indexIdentity,
        IReadOnlyDictionary<string, string> logicalIndexesByPhysicalName) =>
        PostgreSqlNativePlanMapper.Map(
            rawPlan,
            "records",
            "public",
            targetId,
            indexIdentity,
            logicalIndexesByPhysicalName);

    private static ProviderOpaqueIdentity Identity() => new(Guid.NewGuid());
}
