using Groundwork.Kernel;
using Groundwork.Query.Model;
using MongoDB.Bson;
using Xunit;

namespace Groundwork.MongoDb.Tests;

/// <summary>#421: observed sort keys, literal bounds, disk use and root order on MongoDB explains.</summary>
public sealed class MongoNativePlanDetailTests
{
    private static readonly ProviderOpaqueIdentity Target = new(Guid.NewGuid());

    [Fact]
    public void Aggregation_sort_stage_exposes_keys_fused_limit_and_disk_use_in_pipeline_order()
    {
        var explain = new BsonDocument("stages", new BsonArray
        {
            new BsonDocument("$cursor", new BsonDocument("queryPlanner", new BsonDocument
            {
                { "namespace", "db.scope" },
                { "winningPlan", new BsonDocument("stage", "COLLSCAN") }
            })),
            new BsonDocument
            {
                { "$sort", new BsonDocument { { "sortKey", new BsonDocument { { "lastSeen", -1 }, { "id", 1 } } }, { "limit", 21 } } },
                { "usedDisk", true },
                { "spilledRecords", 40 },
                { "spilledDataStorageSize", 4096 }
            },
            new BsonDocument("$limit", 20)
        });

        var forest = Assert.IsType<ProviderPlanForest>(Map(explain));
        Assert.Equal([0, 1, 2], forest.ObservedRootOrder);
        var sort = Assert.Single(forest.Nodes, node => node.Operation == ProviderPlanOperator.TopNSort);
        var details = Assert.IsType<ProviderPlanNodeDetails>(sort.Details);
        Assert.Collection(details.NativeSortKeys!.Value,
            term => { Assert.Equal("lastSeen", term.LogicalColumn); Assert.Equal(OrderDirection.Descending, term.Direction); },
            term => { Assert.Equal("id", term.LogicalColumn); Assert.Equal(OrderDirection.Ascending, term.Direction); });
        Assert.Equal(21, details.NativeLimit.Value);
        Assert.True(details.Spill!.Spilled);
        Assert.Equal(40, details.Spill.SpilledRows);
        Assert.Equal(4096, details.Spill.SpilledBytes);
        var limit = Assert.Single(forest.Nodes, node => node.Operation == ProviderPlanOperator.Limit);
        Assert.Equal(20, limit.Details!.NativeLimit.Value);
    }

    [Fact]
    public void Planner_only_sort_stage_has_keys_and_bound_but_no_spill_fact()
    {
        var explain = new BsonDocument("queryPlanner", new BsonDocument
        {
            { "namespace", "db.scope" },
            { "winningPlan", new BsonDocument
                {
                    { "stage", "SORT" },
                    { "sortPattern", new BsonDocument("status", 1) },
                    { "limitAmount", 5 },
                    { "inputStage", new BsonDocument("stage", "COLLSCAN") }
                } }
        });

        var forest = Assert.IsType<ProviderPlanForest>(Map(explain));
        var sort = Assert.Single(forest.Nodes, node => node.Operation == ProviderPlanOperator.TopNSort);
        Assert.Equal("status", Assert.Single(sort.Details!.NativeSortKeys!.Value).LogicalColumn);
        Assert.Equal(5, sort.Details.NativeLimit.Value);
        Assert.Null(sort.Details.Spill);
        Assert.Equal([0], forest.ObservedRootOrder);
    }

    [Fact]
    public void Renderer_computed_sort_fields_resolve_to_their_source_columns_with_transforms()
    {
        // The renderer's null-rank and ordinal-key $set fields are resolved from their computing stages,
        // never exported under their computed names; a computed field without its stage stays unobserved (#432).
        var ordinalKey = new BsonDocument("$function", new BsonDocument
        {
            { "body", "function(value) { return value; }" },
            { "args", new BsonArray { "$idOrderKey" } },
            { "lang", "js" }
        });
        var nullRank = new BsonDocument("$cond", new BsonArray
        {
            new BsonDocument("$eq", new BsonArray { "$lastSeen", BsonNull.Value }), 0, 1
        });
        var explain = new BsonDocument("stages", new BsonArray
        {
            new BsonDocument("$cursor", new BsonDocument("queryPlanner", new BsonDocument
            {
                { "namespace", "db.scope" },
                { "winningPlan", new BsonDocument("stage", "COLLSCAN") }
            })),
            new BsonDocument("$set", new BsonDocument("_groundwork_null_rank_1", nullRank)),
            new BsonDocument("$set", new BsonDocument("_groundwork_ordinal_key_2", ordinalKey)),
            new BsonDocument("$set", new BsonDocument("_groundwork_order_value_3", "$id")),
            new BsonDocument("$sort", new BsonDocument
            {
                { "sortKey", new BsonDocument { { "_groundwork_null_rank_1", 1 }, { "lastSeen", -1 }, { "_groundwork_ordinal_key_2", 1 }, { "_groundwork_order_value_3", 1 } } },
                { "limit", 21 }
            })
        });
        var forest = Assert.IsType<ProviderPlanForest>(Map(explain));
        var sort = Assert.Single(forest.Nodes, node => node.Operation == ProviderPlanOperator.TopNSort);
        Assert.Collection(sort.Details!.NativeSortKeys!.Value,
            term => { Assert.Equal("lastSeen", term.LogicalColumn); Assert.Equal([ProviderOrderingTransform.NullRank], term.Transforms.ToArray()); },
            term => { Assert.Equal("lastSeen", term.LogicalColumn); Assert.Empty(term.Transforms); Assert.Equal(OrderDirection.Descending, term.Direction); },
            term => { Assert.Equal("idOrderKey", term.LogicalColumn); Assert.Equal([ProviderOrderingTransform.OrdinalStringKey], term.Transforms.ToArray()); Assert.Equal(ProviderPredicateComparison.Ordinal, term.Comparison); },
            term => { Assert.Equal("id", term.LogicalColumn); Assert.Empty(term.Transforms); });
        Assert.DoesNotContain("_groundwork_", System.Text.Json.JsonSerializer.Serialize(sort.Details), StringComparison.Ordinal);

        var missingStage = Map(new BsonDocument("stages", new BsonArray
        {
            new BsonDocument("$cursor", new BsonDocument("queryPlanner", new BsonDocument
            {
                { "namespace", "db.scope" },
                { "winningPlan", new BsonDocument("stage", "COLLSCAN") }
            })),
            new BsonDocument("$sort", new BsonDocument("sortKey", new BsonDocument("_groundwork_ordinal_key_9", 1)))
        }));
        Assert.Null(Assert.Single(Assert.IsType<ProviderPlanForest>(missingStage).Nodes, node => node.Operation == ProviderPlanOperator.Sort).Details);
    }

    [Fact]
    public void Provider_owned_or_nested_sort_fields_leave_keys_unobserved_and_a_bad_literal_fails_the_map()
    {
        var owned = Map(new BsonDocument("queryPlanner", new BsonDocument
        {
            { "namespace", "db.scope" },
            { "winningPlan", new BsonDocument
                {
                    { "stage", "SORT" },
                    { "sortPattern", new BsonDocument("__groundwork_search_name", 1) },
                    { "inputStage", new BsonDocument("stage", "COLLSCAN") }
                } }
        }));
        Assert.Null(Assert.Single(Assert.IsType<ProviderPlanForest>(owned).Nodes, node => node.Operation == ProviderPlanOperator.Sort).Details);

        var nested = Map(new BsonDocument("queryPlanner", new BsonDocument
        {
            { "namespace", "db.scope" },
            { "winningPlan", new BsonDocument
                {
                    { "stage", "SORT" },
                    { "sortPattern", new BsonDocument("body.value", 1) },
                    { "inputStage", new BsonDocument("stage", "COLLSCAN") }
                } }
        }));
        Assert.Null(Assert.Single(Assert.IsType<ProviderPlanForest>(nested).Nodes, node => node.Operation == ProviderPlanOperator.Sort).Details);

        Assert.Null(Map(new BsonDocument("stages", new BsonArray
        {
            new BsonDocument("$cursor", new BsonDocument("queryPlanner", new BsonDocument
            {
                { "namespace", "db.scope" },
                { "winningPlan", new BsonDocument("stage", "COLLSCAN") }
            })),
            new BsonDocument("$limit", 0)
        })));
    }

    private static ProviderPlanForest? Map(BsonDocument explain) =>
        MongoNativePlanMapper.Map(
            explain,
            "db.scope",
            Target,
            _ => new ProviderOpaqueIdentity(Guid.NewGuid()),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));
}
