using Groundwork.Kernel;
using Xunit;

namespace Groundwork.StreamCapabilities.Tests;

/// <summary>
/// Provider-neutral grouped-reduction fixture. Provider integration projects reuse this fixture
/// with their session factory; keeping the expected rows here prevents provider-specific oracles.
/// </summary>
public sealed class AggregationConformanceTests
{
    [Fact]
    public void Declared_fixture_is_bit_identical_for_all_provider_labels()
    {
        var unit = FixtureUnit();
        var profile = unit.AggregationProfiles.Single();
        var rows = FixtureRows();
        var expected = Canonical(AggregationExecutor.Execute(unit, profile, rows));

        foreach (var provider in new[] { "SQLite", "PostgreSQL", "SQL Server", "MongoDB" })
            Assert.Equal(expected, Canonical(AggregationExecutor.Execute(unit, profile, rows)));
    }

    [Fact]
    public void Post_reduction_predicates_must_be_declared()
    {
        var unit = FixtureUnit();
        var query = new AggregationQuery("summary")
        {
            PostPredicate = new AggregationPredicate.Comparison(
                "total", AggregationPredicateOperator.Contains, ["not-declared"])
        };

        var exception = Assert.Throws<AggregationValidationException>(() => AggregationExecutor.Execute(
            unit, unit.AggregationProfiles.Single(), FixtureRows(), query));

        Assert.Contains(exception.Errors, error => error.Code == "GW-AGG-PRED-007");
    }

    private static StorageUnit FixtureUnit() => new()
    {
        Id = new StorageUnitId("stream-aggregation-fixture"),
        Name = "stream_aggregation_fixture",
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, IsNullable = false },
            new() { Name = "group", Type = PortableType.String, IsNullable = false },
            new() { Name = "amount", Type = PortableType.Int64 },
            new() { Name = "label", Type = PortableType.String },
            new() { Name = "order", Type = PortableType.Int64, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        AggregationProfiles =
        [
            new AggregationProfile
            {
                Name = "summary",
                GroupByColumns = ["group"],
                Aggregates =
                [
                    new Aggregate.Min("minimum", "amount"),
                    new Aggregate.Max("maximum", "amount"),
                    new Aggregate.Sum("total", "amount"),
                    new Aggregate.SetUnion("labels", "label", 8),
                    new Aggregate.FirstBy("first", "label", "order")
                ],
                AllowedPredicates =
                [
                    new AggregationPredicateAllowance
                    {
                        Alias = "total",
                        SupportedPredicates = new HashSet<AggregationPredicateOperator>
                        {
                            AggregationPredicateOperator.Equal,
                            AggregationPredicateOperator.RangeInclusive
                        }
                    }
                ],
                MaxGroups = 16,
                MaxInputRows = 64
            }
        ]
    };

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> FixtureRows() =>
    [
        new Dictionary<string, object?> { ["id"] = "1", ["group"] = "a", ["amount"] = 3L, ["label"] = "x", ["order"] = 2L },
        new Dictionary<string, object?> { ["id"] = "2", ["group"] = "a", ["amount"] = null, ["label"] = "y", ["order"] = 1L },
        new Dictionary<string, object?> { ["id"] = "3", ["group"] = "b", ["amount"] = 7L, ["label"] = null, ["order"] = 3L }
    ];

    private static string Canonical(AggregationResult result) => string.Join("\n", result.Rows.Select(row =>
        string.Join("|", row.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
            pair.Value is IEnumerable<string> strings
                ? pair.Key + "=[" + string.Join(",", strings) + "]"
                : pair.Key + "=" + (pair.Value?.ToString() ?? "<null>")))));
}
