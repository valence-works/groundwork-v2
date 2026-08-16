using Groundwork.Kernel;
using Groundwork.Testing;
using Groundwork.Store;
using Xunit;

namespace Groundwork.Testing.SelfTests;

public sealed class AggregationContractTests
{
    [Fact]
    public void In_memory_session_executes_the_declared_profile_and_post_predicate()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("aggregation-memory"),
            Name = "AggregationMemory",
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "group", Type = PortableType.String, IsNullable = false },
                new() { Name = "amount", Type = PortableType.Int32 },
                new() { Name = "order", Type = PortableType.Int64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            AggregationProfiles =
            [
                new AggregationProfile
                {
                    Name = "summary",
                    GroupByColumns = ["group"],
                    Aggregates = [new Aggregate.Sum("total", "amount")],
                    AllowedPredicates =
                    [
                        new AggregationPredicateAllowance
                        {
                            Alias = "total",
                            SupportedPredicates = new HashSet<AggregationPredicateOperator>
                            {
                                AggregationPredicateOperator.RangeInclusive
                            }
                        }
                    ]
                }
            ]
        };
        var factory = new InMemoryProviderFactory();
        using var connection = factory.Create("memory://aggregation");
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "a1", ["group"] = "a", ["amount"] = 2, ["order"] = 1L
        }));
        session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "a2", ["group"] = "a", ["amount"] = 3, ["order"] = 2L
        }));
        session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "b1", ["group"] = "b", ["amount"] = 1, ["order"] = 3L
        }));

        var result = session.Aggregate(new AggregationQuery("summary")
        {
            PostPredicate = new AggregationPredicate.Comparison(
                "total",
                AggregationPredicateOperator.RangeInclusive,
                [5L, 5L])
        });

        var row = Assert.Single(result.Rows);
        Assert.Equal("a", row["group"]);
        Assert.Equal(5L, Assert.IsType<long>(row["total"]));
    }

    [Fact]
    public void In_memory_scoped_aggregation_binds_scope_to_value_fingerprint()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("aggregation-scoped-memory"),
            Name = "AggregationScopedMemory",
            Scope = ScopePolicy.Scoped,
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "group", Type = PortableType.String, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            AggregationProfiles =
            [
                new AggregationProfile
                {
                    Name = "summary",
                    GroupByColumns = ["group"],
                    Aggregates = [new Aggregate.Count("count")]
                }
            ]
        };
        using var connection = new InMemoryProviderFactory().Create("memory://aggregation-scoped");
        connection.Schema.Apply(unit);
        var first = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("tenant-a")));
        var second = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("tenant-b")));
        first.Insert(new StorageValues(new Dictionary<string, object?> { ["id"] = "same", ["group"] = "g" }));
        second.Insert(new StorageValues(new Dictionary<string, object?> { ["id"] = "same", ["group"] = "g" }));

        var query = new AggregationQuery("summary");
        var firstResult = first.Aggregate(query);
        var secondResult = second.Aggregate(query);

        Assert.Equal(firstResult.ShapeFingerprint, secondResult.ShapeFingerprint);
        Assert.NotEqual(firstResult.ValueFingerprint, secondResult.ValueFingerprint);
    }
}
