using Groundwork.Kernel;
using Xunit;

namespace Groundwork.Kernel.Tests;

/// <summary>
/// JSON values are storable but opaque: they cannot participate in a portable comparison
/// position. Index declarations retain their existing declaration diagnostic and are intentionally
/// not repeated as GW-PORT-012 portability findings.
/// </summary>
public sealed class StorageOnlyJsonTests
{
    [Fact]
    public void A_json_key_column_is_refused_by_portability_validation()
    {
        var unit = Unit(
            [
                new ColumnDefinition { Name = "payload", Type = PortableType.Json }
            ],
            key: ["payload"]);

        var refusal = Assert.Single(
            PortabilityValidator.Validate(unit).Refusals,
            finding => finding.Code == "GW-PORT-012");

        Assert.Equal("key.columns[0]", refusal.Path);
        Assert.Contains("Json column 'payload'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("a key column", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_json_group_by_column_is_refused_by_portability_validation()
    {
        var unit = Unit(
            [
                new ColumnDefinition { Name = "id", Type = PortableType.Guid, IsNullable = false },
                new ColumnDefinition { Name = "payload", Type = PortableType.Json }
            ],
            key: ["id"]) with
        {
            AggregationProfiles =
            [
                new AggregationProfile
                {
                    Name = "by_payload",
                    GroupByColumns = ["payload"],
                    Aggregates = [new Aggregate.Count("rows")],
                    MaxGroups = 10,
                    MaxInputRows = 10
                }
            ]
        };

        var refusal = Assert.Single(
            PortabilityValidator.Validate(unit).Refusals,
            finding => finding.Code == "GW-PORT-012");

        Assert.Equal("aggregationProfiles.by_payload.groupByColumns", refusal.Path);
        Assert.Contains("Json column 'payload'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("a group-by column of aggregation profile 'by_payload'", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_json_index_is_not_repeated_as_a_portability_refusal()
    {
        var unit = Unit(
            [
                new ColumnDefinition { Name = "id", Type = PortableType.Guid, IsNullable = false },
                new ColumnDefinition { Name = "payload", Type = PortableType.Json }
            ],
            indexes: [new IndexDefinition
            {
                Name = "by_payload",
                Columns = [new IndexColumn("payload")]
            }],
            key: ["id"]);

        Assert.DoesNotContain(
            PortabilityValidator.Validate(unit).Refusals,
            finding => finding.Code == "GW-PORT-012");
    }

    private static StorageUnit Unit(
        ColumnDefinition[] columns,
        IndexDefinition[]? indexes = null,
        string[]? key = null) => new()
        {
            Id = new StorageUnitId("json-comparison-positions"),
            Name = "json_comparison_positions",
            Columns = columns,
            Indexes = indexes ?? [],
            Key = new KeyDefinition { Columns = key ?? [columns[0].Name] }
        };
}
