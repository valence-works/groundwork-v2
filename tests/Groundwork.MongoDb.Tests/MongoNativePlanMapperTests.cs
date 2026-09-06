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

    private static BsonDocument Explain(string winningPlan) =>
        BsonDocument.Parse($"{{\"queryPlanner\":{{\"namespace\":\"db.scope\",\"winningPlan\":{winningPlan}}}}}");

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
