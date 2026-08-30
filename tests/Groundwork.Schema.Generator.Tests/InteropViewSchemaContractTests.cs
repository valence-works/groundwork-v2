using Groundwork.Schema;
using Xunit;

namespace Groundwork.Schema.Generator.Tests;

public sealed class InteropViewSchemaContractTests
{
    [Fact]
    public void A_blank_interop_view_is_not_silently_treated_as_no_declaration()
    {
        var refusal = Assert.Throws<ArgumentException>(() => new SchemaTable(
            "orders",
            [new SchemaColumn("id", SchemaValueType.Guid, isNullable: false)],
            ["id"],
            interopView: " "));

        Assert.Equal("interopView", refusal.ParamName);
    }

    [Fact]
    public void Canonical_schema_round_trip_preserves_an_interop_view_byte_for_byte()
    {
        var document = new SchemaDocument(
        [
            new SchemaTable(
                "orders",
                [new SchemaColumn("id", SchemaValueType.Guid, isNullable: false)],
                ["id"],
                interopView: "reporting_orders")
        ]);

        var canonical = GroundworkSchemaCanonical.Serialize(document);
        var restored = GroundworkSchemaCanonical.Read(canonical);

        Assert.Equal("reporting_orders", Assert.Single(restored.Tables).InteropView);
        Assert.Equal(canonical, GroundworkSchemaCanonical.Serialize(restored));
        Assert.Equal(
            GroundworkSchemaCanonical.Fingerprint(document),
            GroundworkSchemaCanonical.Fingerprint(restored));
        Assert.Contains("\"interopView\":\"reporting_orders\"", canonical, StringComparison.Ordinal);
    }
}
