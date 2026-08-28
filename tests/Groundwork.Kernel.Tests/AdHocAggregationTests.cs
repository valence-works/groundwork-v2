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

    [Fact]
    public void Ad_hoc_query_enforces_acceptance_group_and_input_budgets()
    {
        var unit = Unit();
        var rows = new[] { Row("a", 1), Row("b", 2) };
        var acceptance = AggregationAcceptance.Allow(
            "GW-AGG-0004", "bounded report", "operations", DateTimeOffset.UtcNow.AddDays(1),
            maxGroups: 1, maxInputRows: 10);
        var groups = AggregationQuery.ForAdHoc("budgeted", ["team"], [new Aggregate.Count("count")], acceptance);

        var groupOverflow = Assert.Throws<AggregationBudgetExceededException>(() =>
            AggregationExecutor.Execute(unit, rows, groups));
        Assert.Equal("GW-AGG-BOUND-005", groupOverflow.Code);

        var rowBound = AggregationAcceptance.Allow(
            "GW-AGG-0006", "bounded input report", "operations", DateTimeOffset.UtcNow.AddDays(1),
            maxGroups: 10, maxInputRows: 1);
        var rowsQuery = AggregationQuery.ForAdHoc("budgeted", ["team"], [new Aggregate.Count("count")], rowBound);
        var inputOverflow = Assert.Throws<AggregationBudgetExceededException>(() =>
            AggregationExecutor.Execute(unit, rows, rowsQuery));
        Assert.Equal("GW-AGG-BOUND-004", inputOverflow.Code);
    }

    [Fact]
    public void Ad_hoc_aliases_use_portable_identifier_and_reserved_name_refusals()
    {
        var unit = Unit();
        var acceptance = AggregationAcceptance.Allow(
            "GW-AGG-0005", "alias validation", "operations", DateTimeOffset.UtcNow.AddDays(1), 10, 10);
        var tooLong = AggregationQuery.ForAdHoc(
            "invalid-aliases", [new AggregationGroup.Column("team")],
            [new Aggregate.Count(new string('x', PortabilityValidator.MaximumPortableIdentifierLength + 1))], acceptance);
        var lengthError = Assert.Throws<AggregationValidationException>(() =>
            AggregationExecutor.Execute(unit, [Row("a", 1)], tooLong));
        Assert.Contains(lengthError.Errors, error => error.Code == "GW-PORT-010");

        var tooLongGroup = AggregationQuery.ForAdHoc(
            "invalid-aliases", [new AggregationGroup.Column(new string('x', PortabilityValidator.MaximumPortableIdentifierLength + 1))],
            [new Aggregate.Count("count")], acceptance);
        var groupLengthError = Assert.Throws<AggregationValidationException>(() =>
            AggregationExecutor.Execute(unit, [Row("a", 1)], tooLongGroup));
        Assert.Contains(groupLengthError.Errors, error => error.Code == "GW-PORT-010");

        var reserved = AggregationQuery.ForAdHoc(
            "invalid-aliases", [new AggregationGroup.Column("__groundwork_aggregation_group")],
            [new Aggregate.Count("count")], acceptance);
        var reservedError = Assert.Throws<AggregationValidationException>(() =>
            AggregationExecutor.Execute(unit, [Row("a", 1)], reserved));
        Assert.Contains(reservedError.Errors, error => error.Code == "GW-AGG-DECL-009");

        var reservedAggregate = AggregationQuery.ForAdHoc(
            "invalid-aliases", [new AggregationGroup.Column("team")],
            [new Aggregate.Count("__groundwork_aggregation_count")], acceptance);
        var reservedAggregateError = Assert.Throws<AggregationValidationException>(() =>
            AggregationExecutor.Execute(unit, [Row("a", 1)], reservedAggregate));
        Assert.Contains(reservedAggregateError.Errors, error => error.Code == "GW-AGG-DECL-010");
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
