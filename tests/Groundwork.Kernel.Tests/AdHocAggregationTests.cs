using Groundwork.Kernel;
using Xunit;

namespace Groundwork.Kernel.Tests;

public sealed class AdHocAggregationTests
{
    [Fact]
    public void Acceptance_requires_identity_reason_owner_expiry_and_positive_budgets()
    {
        var acceptance = AggregationAcceptance.Allow(
            "GW-AGG-0001",
            "temporary support report",
            "operations",
            new DateTimeOffset(2027, 1, 1, 12, 30, 0, TimeSpan.FromHours(2)),
            maxGroups: 20,
            maxInputRows: 200);

        Assert.True(acceptance.Allowed);
        Assert.Equal("GW-AGG-0001", acceptance.Id);
        Assert.Equal("temporary support report", acceptance.Reason);
        Assert.Equal("operations", acceptance.Owner);
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), acceptance.ExpiresOn);
        Assert.Equal(20, acceptance.MaxGroups);
        Assert.Equal(200, acceptance.MaxInputRows);
        Assert.Throws<ArgumentException>(() => AggregationAcceptance.Allow(
            "aggregation", "reason", "owner", acceptance.ExpiresOn!.Value, 1, 1));
        Assert.Throws<ArgumentException>(() => AggregationAcceptance.Allow(
            "GW-AGG-0001", "", "owner", acceptance.ExpiresOn!.Value, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AggregationAcceptance.Allow(
            "GW-AGG-0001", "reason", "owner", acceptance.ExpiresOn!.Value, 0, 1));
    }

    [Fact]
    public void Ad_hoc_query_composes_closed_groups_and_reducers_and_uses_acceptance_budgets()
    {
        var unit = Unit();
        var query = AggregationQuery.ForAdHoc(
            "support-summary",
            ["team"],
            [new Aggregate.Count("count"), new Aggregate.Sum("total", "amount")],
            AggregationAcceptance.Allow(
                "GW-AGG-0002", "temporary support report", "operations",
                DateTimeOffset.UtcNow.AddDays(2), maxGroups: 5, maxInputRows: 10));

        var result = AggregationExecutor.Execute(unit,
            [
                Row("a", 2),
                Row("a", 3),
                Row("b", 7)
            ], query);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(2L, result.Rows.Single(row => Equals(row["team"], "a"))["count"]);
        Assert.Equal(5L, result.Rows.Single(row => Equals(row["team"], "a"))["total"]);
    }

    [Fact]
    public void Ad_hoc_query_refuses_without_acceptance_or_after_expiry()
    {
        var unit = Unit();
        var profile = new AggregationProfile
        {
            Name = "support-summary",
            GroupByColumns = ["team"],
            Aggregates = [new Aggregate.Count("count")]
        };
        var refused = AggregationQuery.ForAdHoc(profile, AggregationAcceptance.Refuse);
        var missing = Assert.Throws<AggregationValidationException>(() => AggregationExecutor.Execute(
            unit, [Row("a", 1)], refused));
        Assert.Contains(missing.Errors, error => error.Code == "GW-AGG-ADHOC-001");

        var expired = AggregationQuery.ForAdHoc(profile, AggregationAcceptance.Allow(
            "GW-AGG-0003", "expired report", "operations", DateTimeOffset.UtcNow.AddDays(-1), 5, 10));
        var expiry = Assert.Throws<AggregationValidationException>(() => AggregationExecutor.Execute(
            unit, [Row("a", 1)], expired));
        Assert.Contains(expiry.Errors, error => error.Code == "GW-AGG-ADHOC-002");
    }

    private static StorageUnit Unit() => new()
    {
        Id = new StorageUnitId("adhoc-aggregation"),
        Name = "adhoc_aggregation",
        Columns =
        [
            new() { Name = "id", Type = PortableType.Int64, IsNullable = false },
            new() { Name = "team", Type = PortableType.String, IsNullable = false },
            new() { Name = "amount", Type = PortableType.Int32, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static IReadOnlyDictionary<string, object?> Row(string team, int amount) =>
        new Dictionary<string, object?>
        {
            ["id"] = amount,
            ["team"] = team,
            ["amount"] = amount
        };
}
