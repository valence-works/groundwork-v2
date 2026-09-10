using Groundwork.Kernel;
using MongoDB.Bson;
using Xunit;

namespace Groundwork.MongoDb.Tests;

public sealed class MongoNativePlanMapperTests
{
    private static readonly ProviderOpaqueIdentity Target =
        new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

    [Fact]
    public void Classic_fetch_and_index_scan_preserve_parentage_and_exact_index_mapping()
    {
        var indexIds = new Dictionary<string, ProviderOpaqueIdentity>(StringComparer.Ordinal);
        var forest = Map(
            Explain("{\"stage\":\"FETCH\",\"inputStage\":{\"stage\":\"IXSCAN\",\"indexName\":\"status_1\"}}"),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["status_1"] = "by_status" },
            indexIds);

        Assert.NotNull(forest);
        var fetch = Assert.Single(forest!.Nodes, node => node.Operation == ProviderPlanOperator.Materialize);
        var index = Assert.Single(forest.Nodes, node => node.Operation == ProviderPlanOperator.IndexScan);
        Assert.Null(fetch.ParentId);
        Assert.Equal(fetch.Id, index.ParentId);
        Assert.Null(fetch.TargetId);
        Assert.Equal(Target, index.TargetId);
        Assert.NotNull(index.IndexId);
        Assert.Equal("by_status", index.LogicalIndexName);
        Assert.Null(index.IsCovering);
        Assert.Single(indexIds);
    }

    [Fact]
    public void Classic_fused_sort_limit_maps_to_one_top_n_node_and_preserves_parentage()
    {
        var winningPlan = new BsonDocument
        {
            { "stage", "SORT" },
            { "limitAmount", new BsonInt64(2) },
            { "inputStage", new BsonDocument("stage", "IXSCAN") { { "indexName", "status_1" } } }
        };
        var forest = Map(
            Explain(winningPlan),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["status_1"] = "by_status" });

        Assert.NotNull(forest);
        var topN = Assert.Single(forest!.Nodes, node => node.Operation == ProviderPlanOperator.TopNSort);
        var index = Assert.Single(forest.Nodes, node => node.Operation == ProviderPlanOperator.IndexScan);
        Assert.Equal(topN.Id, index.ParentId);
        Assert.Null(topN.TargetId);
        Assert.Null(topN.IndexId);
        Assert.Null(topN.SortPurpose);
    }

    [Fact]
    public void Sbe_query_plan_is_mapped_without_exporting_native_ids_or_values()
    {
        var forest = Map(Explain(
            "{\"queryPlan\":{\"stage\":\"LIMIT\",\"planNodeId\":4,\"inputStage\":{\"stage\":\"IXSCAN\",\"planNodeId\":5,\"indexName\":\"_id_\",\"indexBounds\":{\"_id\":[\"secret-value\"]}}},\"slotBasedPlan\":{\"stages\":\"secret-native-text\"}}"));

        Assert.NotNull(forest);
        Assert.Equal(new[] { 0, 1 }, forest!.Nodes.Select(node => node.Id));
        var limit = Assert.Single(forest.Nodes, node => node.Operation == ProviderPlanOperator.Limit);
        var index = Assert.Single(forest.Nodes, node => node.Operation == ProviderPlanOperator.IndexScan);
        Assert.Equal(limit.Id, index.ParentId);
        Assert.DoesNotContain("secret-value", string.Join(" ", forest.Nodes));
        Assert.DoesNotContain("secret-native-text", string.Join(" ", forest.Nodes));
    }

    /// <summary>The `_id` fast path (classic IDHACK, or the 8.0 express scan that names `_id_`) is one key search of the target.</summary>
    [Theory]
    [InlineData("{\"stage\":\"IDHACK\"}")]
    [InlineData("{\"stage\":\"EXPRESS_IXSCAN\",\"indexName\":\"_id_\"}")]
    public void Id_fast_path_maps_to_one_primary_key_search(string winningPlan)
    {
        var forest = Map(Explain(winningPlan));

        var access = Assert.Single(Assert.IsType<ProviderPlanForest>(forest).Nodes);
        Assert.Equal(ProviderPlanOperator.PrimaryKeySearch, access.Operation);
        Assert.Equal(Target, access.TargetId);
        Assert.Null(access.IndexId);
        Assert.Null(access.LogicalIndexName);
        Assert.Null(access.ParentId);
    }

    [Theory]
    [InlineData("{\"stage\":\"IDHACK\",\"indexName\":\"_id_\"}")]
    [InlineData("{\"stage\":\"IDHACK\",\"inputStage\":{\"stage\":\"COLLSCAN\"}}")]
    [InlineData("{\"stage\":\"EXPRESS_IXSCAN\"}")]
    public void Malformed_id_fast_path_withholds_the_forest(string winningPlan) =>
        Assert.Null(Map(Explain(winningPlan)));

    /// <summary>An express scan of another unique index is an index search, not a primary-key search.</summary>
    [Fact]
    public void Express_scan_of_a_secondary_index_maps_to_an_index_search()
    {
        var forest = Map(Explain("{\"stage\":\"EXPRESS_IXSCAN\",\"indexName\":\"status_1\"}"),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["status_1"] = "by_status" });

        var access = Assert.Single(Assert.IsType<ProviderPlanForest>(forest).Nodes);
        Assert.Equal(ProviderPlanOperator.IndexSearch, access.Operation);
        Assert.Equal("by_status", access.LogicalIndexName);
    }

    /// <summary>A keyset `$or` explodes into one bounded scan per branch merged on the sort pattern (SORT_MERGE): an ordered merge, not a blocking sort.</summary>
    [Fact]
    public void Sort_merge_of_index_scans_maps_to_an_ordered_merge_with_its_keys()
    {
        var forest = Map(Explain(
            "{\"stage\":\"LIMIT\",\"limitAmount\":128,\"inputStage\":{\"stage\":\"FETCH\",\"inputStage\":{\"stage\":\"SORT_MERGE\",\"sortPattern\":{\"startTime\":1,\"sequence\":1},\"inputStages\":[{\"stage\":\"FETCH\",\"inputStage\":{\"stage\":\"IXSCAN\",\"indexName\":\"trace_1\"}},{\"stage\":\"IXSCAN\",\"indexName\":\"trace_1\"}]}}}"),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["trace_1"] = "trace_detail" });

        var nodes = Assert.IsType<ProviderPlanForest>(forest).Nodes;
        Assert.Equal(
            new[] { ProviderPlanOperator.Limit, ProviderPlanOperator.Materialize, ProviderPlanOperator.MergeOrdered, ProviderPlanOperator.Materialize, ProviderPlanOperator.IndexScan, ProviderPlanOperator.IndexScan },
            nodes.Select(node => node.Operation).ToArray());
        var merge = nodes[2];
        Assert.Equal(1, merge.ParentId);
        Assert.Null(merge.TargetId);
        Assert.Collection(Assert.IsType<ProviderPlanNodeDetails>(merge.Details).NativeSortKeys!.Value,
            key => { Assert.Equal("startTime", key.LogicalColumn); Assert.Equal(Groundwork.Query.Model.OrderDirection.Ascending, key.Direction); },
            key => { Assert.Equal("sequence", key.LogicalColumn); Assert.Equal(Groundwork.Query.Model.OrderDirection.Ascending, key.Direction); });
        Assert.Equal(merge.Id, nodes[3].ParentId);
        Assert.Equal(merge.Id, nodes[5].ParentId);
        Assert.All(nodes.Where(node => node.Operation == ProviderPlanOperator.IndexScan), scan =>
        {
            Assert.Equal(Target, scan.TargetId);
            Assert.Equal("trace_detail", scan.LogicalIndexName);
        });
    }

    [Theory]
    [InlineData("{\"stage\":\"SORT_MERGE\",\"sortPattern\":{\"startTime\":1},\"inputStages\":[{\"stage\":\"IXSCAN\",\"indexName\":\"trace_1\"}]}")]
    [InlineData("{\"stage\":\"SORT_MERGE\",\"inputStages\":[{\"stage\":\"IXSCAN\",\"indexName\":\"trace_1\"},{\"stage\":\"IXSCAN\",\"indexName\":\"trace_1\"}]}")]
    [InlineData("{\"stage\":\"SORT_MERGE\",\"sortPattern\":{\"startTime\":\"asc\"},\"inputStages\":[{\"stage\":\"IXSCAN\",\"indexName\":\"trace_1\"},{\"stage\":\"IXSCAN\",\"indexName\":\"trace_1\"}]}")]
    public void Malformed_sort_merge_withholds_the_forest(string winningPlan) =>
        Assert.Null(Map(Explain(winningPlan), new Dictionary<string, string>(StringComparer.Ordinal) { ["trace_1"] = "trace_detail" }));

    [Fact]
    public void Collection_scan_maps_the_actual_target_without_index_claims()
    {
        var forest = Map(Explain("{\"stage\":\"COLLSCAN\",\"direction\":\"forward\"}"));

        var scan = Assert.Single(forest!.Nodes);
        Assert.Equal(ProviderPlanOperator.TableScan, scan.Operation);
        Assert.Equal(Target, scan.TargetId);
        Assert.Null(scan.IndexId);
        Assert.Null(scan.LogicalIndexName);
        Assert.Null(scan.IsCovering);
    }

    [Fact]
    public void Covered_projection_marks_only_the_actual_index_access()
    {
        var forest = Map(Explain(
            "{\"stage\":\"PROJECTION_COVERED\",\"transformBy\":{\"status\":1},\"inputStage\":{\"stage\":\"IXSCAN\",\"indexName\":\"status_1\"}}"));

        Assert.NotNull(forest);
        var projection = Assert.Single(forest!.Nodes, node => node.Operation == ProviderPlanOperator.Projection);
        var index = Assert.Single(forest.Nodes, node => node.Operation == ProviderPlanOperator.IndexScan);
        Assert.Equal(projection.Id, index.ParentId);
        Assert.True(index.IsCovering == true);
        Assert.Null(projection.IsCovering);
    }

    [Fact]
    public void Covered_projection_with_fetch_is_withheld_as_contradictory()
    {
        var forest = Map(Explain(
            "{\"stage\":\"PROJECTION_COVERED\",\"inputStage\":{\"stage\":\"FETCH\",\"inputStage\":{\"stage\":\"IXSCAN\",\"indexName\":\"status_1\"}}}"));

        Assert.Null(forest);
    }

    [Fact]
    public void Aggregation_cursor_and_supported_pipeline_stages_are_sibling_roots()
    {
        var explain = BsonDocument.Parse("""
        {
          "stages": [
            { "$cursor": { "queryPlanner": {
              "namespace": "db.scope",
              "winningPlan": { "stage": "COLLSCAN" }
            } }, "nReturned": 1, "executionTimeMillisEstimate": 0 },
            { "$set": { "__gw_null_rank": { "$cond": [true, 0, 1] } } },
            { "$sort": { "status": 1 }, "nReturned": 1, "executionTimeMillisEstimate": 0,
              "totalDataSizeSortedBytesEstimate": 0, "usedDisk": false },
            { "$project": { "status": 1 } },
            { "$skip": 2 },
            { "$limit": 3 }
          ]
        }
        """);

        var forest = Map(explain);

        Assert.NotNull(forest);
        Assert.Equal(
            new[]
            {
                ProviderPlanOperator.TableScan,
                ProviderPlanOperator.Compute,
                ProviderPlanOperator.Sort,
                ProviderPlanOperator.Projection,
                ProviderPlanOperator.Offset,
                ProviderPlanOperator.Limit
            },
            forest!.Nodes.Select(node => node.Operation));
        Assert.All(forest.Nodes, node => Assert.Null(node.ParentId));
        Assert.Equal(Target, forest.Nodes[0].TargetId);
        Assert.All(forest.Nodes.Skip(1), node => Assert.Null(node.TargetId));
    }

    [Fact]
    public void Aggregation_fused_sort_limit_maps_to_one_top_n_sibling_root()
    {
        var explain = BsonDocument.Parse("""
        {
          "stages": [
            { "$cursor": { "queryPlanner": {
              "namespace": "db.scope",
              "winningPlan": { "stage": "COLLSCAN" }
            } } },
            { "$sort": { "sortKey": { "status": 1 }, "limit": 2 } }
          ]
        }
        """);

        var forest = Map(explain);

        Assert.NotNull(forest);
        Assert.Collection(
            forest!.Nodes,
            node => Assert.Equal(ProviderPlanOperator.TableScan, node.Operation),
            node =>
            {
                Assert.Equal(ProviderPlanOperator.TopNSort, node.Operation);
                Assert.Null(node.ParentId);
                Assert.Null(node.TargetId);
                Assert.Null(node.IndexId);
                Assert.Null(node.SortPurpose);
            });
    }

    [Fact]
    public void Physical_index_lookup_is_exact_and_unknown_names_remain_opaque()
    {
        var indexIds = new Dictionary<string, ProviderOpaqueIdentity>(StringComparer.Ordinal);
        var forest = Map(
            Explain("{\"stage\":\"IXSCAN\",\"indexName\":\"idx\"}"),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["idx_extra"] = "wrong" },
            indexIds);

        var index = Assert.Single(forest!.Nodes);
        Assert.NotNull(index.IndexId);
        Assert.Null(index.LogicalIndexName);
        Assert.Single(indexIds);
        Assert.Equal("idx", indexIds.Keys.Single());
    }

    [Fact]
    public void Wrong_namespace_and_unknown_pipeline_operator_withhold_the_whole_forest()
    {
        Assert.Null(Map(BsonDocument.Parse("""
        { "queryPlanner": {
          "namespace": "db.other",
          "winningPlan": { "stage": "COLLSCAN" }
        } }
        """)));

        Assert.Null(Map(BsonDocument.Parse("""
        { "stages": [
          { "$cursor": { "queryPlanner": {
            "namespace": "db.scope",
            "winningPlan": { "stage": "COLLSCAN" }
          } } },
          { "$unwind": { "path": "$status" } }
        ] }
        """)));
    }

    [Theory]
    [InlineData("{\"stage\":\"FETCH\",\"inputStage\":{\"stage\":\"UNKNOWN\"}}")]
    [InlineData("{\"stage\":\"COLLSCAN\",\"inputStage\":{\"stage\":\"IXSCAN\",\"indexName\":\"idx\"}}")]
    [InlineData("{\"stage\":\"FETCH\",\"inputStage\":{\"stage\":\"IXSCAN\",\"indexName\":\"idx\"},\"thenStage\":{\"stage\":\"COLLSCAN\"}}")]
    [InlineData("{\"stage\":\"SORT\",\"inputStage\":{\"stage\":\"IXSCAN\",\"indexName\":\"idx\"},\"inputStages\":[]}")]
    [InlineData("{\"stage\":\"IXSCAN\",\"indexName\":\"idx\",\"inputStage\":{\"stage\":\"COLLSCAN\"}}")]
    [InlineData("{\"stage\":\"SORT\",\"planNodeId\":1,\"inputStage\":{\"stage\":\"SORT\",\"planNodeId\":1,\"inputStage\":{\"stage\":\"IXSCAN\",\"indexName\":\"idx\"}}}")]
    public void Malformed_or_unmapped_native_shapes_withhold_the_whole_forest(string winningPlan)
    {
        Assert.Null(Map(Explain(winningPlan)));
    }

    [Fact]
    public void Winning_plan_is_not_discovered_inside_rejected_plans()
    {
        var explain = BsonDocument.Parse("""
        { "queryPlanner": {
          "namespace": "db.scope",
          "rejectedPlans": [ { "stage": "COLLSCAN" } ]
        } }
        """);

        Assert.Null(Map(explain));
    }

    [Fact]
    public void Aggregation_requires_one_cursor_and_valid_stage_payloads()
    {
        var noCursor = BsonDocument.Parse("""
        { "stages": [ { "$limit": 1 } ] }
        """);
        var duplicateCursor = BsonDocument.Parse("""
        { "stages": [
          { "$cursor": { "queryPlanner": {
            "namespace": "db.scope", "winningPlan": { "stage": "COLLSCAN" }
          } } },
          { "$cursor": { "queryPlanner": {
            "namespace": "db.scope", "winningPlan": { "stage": "COLLSCAN" }
          } } }
        ] }
        """);
        var badLimit = BsonDocument.Parse("""
        { "stages": [
          { "$cursor": { "queryPlanner": {
            "namespace": "db.scope", "winningPlan": { "stage": "COLLSCAN" }
          } } },
          { "$limit": "not-a-number" }
        ] }
        """);
        var cursor_not_first = BsonDocument.Parse("""
        { "stages": [
          { "$set": { "status": "Open" } },
          { "$cursor": { "queryPlanner": {
            "namespace": "db.scope", "winningPlan": { "stage": "COLLSCAN" }
          } } }
        ] }
        """);
        var unknownMetadata = BsonDocument.Parse("""
        { "stages": [
          { "$cursor": { "queryPlanner": {
            "namespace": "db.scope", "winningPlan": { "stage": "COLLSCAN" }
          } } },
          { "$sort": { "status": 1 }, "unrecognizedCounter": 1 }
        ] }
        """);

        Assert.Null(Map(noCursor));
        Assert.Null(Map(duplicateCursor));
        Assert.Null(Map(badLimit));
        Assert.Null(Map(cursor_not_first));
        Assert.Null(Map(unknownMetadata));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"2\"")]
    [InlineData("null")]
    [InlineData("true")]
    public void Malformed_classic_fused_limit_amount_withholds_the_whole_forest(string limitAmount)
    {
        Assert.Null(Map(ClassicSortExplain(ParseJsonValue(limitAmount))));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"2\"")]
    public void Malformed_pipeline_fused_limit_withholds_the_whole_forest(string limit)
    {
        Assert.Null(Map(AggregationSortExplain(ParseJsonValue(limit))));
    }

    [Fact]
    public void Duplicate_classic_fused_limit_amount_fields_withhold_the_whole_forest()
    {
        var winningPlan = new BsonDocument { { "stage", "SORT" } };
        winningPlan.AllowDuplicateNames = true;
        winningPlan.Add("limitAmount", 1);
        winningPlan.Add("limitAmount", 2);
        winningPlan.Add("inputStage", new BsonDocument("stage", "COLLSCAN"));

        Assert.Null(Map(Explain(winningPlan)));
    }

    [Fact]
    public void Pipeline_fused_sort_rejects_the_classic_limit_amount_alias()
    {
        var sort = new BsonDocument
        {
            { "sortKey", new BsonDocument("status", 1) },
            { "limit", 2 },
            { "limitAmount", 2 }
        };

        Assert.Null(Map(AggregationSortExplain(sort)));
    }

    [Fact]
    public void Classic_fused_sort_rejects_the_pipeline_limit_alias()
    {
        var winningPlan = new BsonDocument
        {
            { "stage", "SORT" },
            { "limit", 2 },
            { "inputStage", new BsonDocument("stage", "COLLSCAN") }
        };

        Assert.Null(Map(Explain(winningPlan)));
    }

    [Fact]
    public void Pipeline_fused_sort_requires_the_documented_sort_key()
    {
        Assert.Null(Map(AggregationSortExplain(new BsonDocument("limit", 2))));
    }

    private static BsonDocument Explain(string winningPlan) =>
        BsonDocument.Parse($"{{\"queryPlanner\":{{\"namespace\":\"db.scope\",\"winningPlan\":{winningPlan}}}}}");

    private static BsonDocument Explain(BsonDocument winningPlan) =>
        new("queryPlanner", new BsonDocument
        {
            { "namespace", "db.scope" },
            { "winningPlan", winningPlan }
        });

    private static BsonDocument ClassicSortExplain(BsonValue limitAmount) =>
        Explain(new BsonDocument
        {
            { "stage", "SORT" },
            { "limitAmount", limitAmount },
            { "inputStage", new BsonDocument("stage", "COLLSCAN") }
        });

    private static BsonDocument AggregationSortExplain(BsonDocument sort) =>
        new("stages", new BsonArray
        {
            new BsonDocument("$cursor", new BsonDocument("queryPlanner", new BsonDocument
            {
                { "namespace", "db.scope" },
                { "winningPlan", new BsonDocument("stage", "COLLSCAN") }
            })),
            new BsonDocument("$sort", sort)
        });

    private static BsonDocument AggregationSortExplain(BsonValue limit) =>
        AggregationSortExplain(new BsonDocument
        {
            { "sortKey", new BsonDocument("status", 1) },
            { "limit", limit }
        });

    private static BsonValue ParseJsonValue(string value) =>
        BsonDocument.Parse("{\"value\":" + value + "}")["value"];

    private static ProviderPlanForest? Map(
        BsonDocument explain,
        IReadOnlyDictionary<string, string>? logicalIndexes = null,
        Dictionary<string, ProviderOpaqueIdentity>? indexIds = null)
    {
        indexIds ??= new Dictionary<string, ProviderOpaqueIdentity>(StringComparer.Ordinal);
        logicalIndexes ??= new Dictionary<string, string>(StringComparer.Ordinal);
        return MongoNativePlanMapper.Map(
            explain,
            "db.scope",
            Target,
            physicalName =>
            {
                if (!indexIds.TryGetValue(physicalName, out var identity))
                {
                    identity = new ProviderOpaqueIdentity(Guid.NewGuid());
                    indexIds.Add(physicalName, identity);
                }

                return identity;
            },
            logicalIndexes);
    }
}
