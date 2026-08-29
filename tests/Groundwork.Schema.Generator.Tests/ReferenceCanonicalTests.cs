using Groundwork.Schema;
using Groundwork.SchemaTool;
using Xunit;

namespace Groundwork.Schema.Generator.Tests;

public sealed class ReferenceCanonicalTests
{
    [Fact]
    public void Canonical_schema_round_trips_references_and_compiles_them_to_kernel_metadata()
    {
        var target = new SchemaTable(
            "customers",
            [new SchemaColumn("id", SchemaValueType.Guid, isNullable: false)],
            ["id"]);
        var source = new SchemaTable(
            "orders",
            [
                new SchemaColumn("id", SchemaValueType.Guid, isNullable: false),
                new SchemaColumn("customer_id", SchemaValueType.Guid, isNullable: false)
            ],
            ["id"],
            indexes: [new SchemaIndex("by_customer", [new SchemaIndexColumn("customer_id")])],
            references: [new SchemaReference("customer", "customers", ["customer_id"])]);
        var schema = new SchemaDocument([source, target]);

        var canonical = GroundworkSchemaCanonical.Serialize(schema);
        var roundTrip = GroundworkSchemaCanonical.Parse(canonical);
        var compiled = SchemaCompilation.Compile(roundTrip);

        Assert.Contains("\"references\":[{\"name\":\"customer\",\"target\":\"customers\",\"columns\":[\"customer_id\"]}]", canonical, StringComparison.Ordinal);
        var reference = Assert.Single(compiled.Single(unit => unit.Id.Value == "orders").References);
        Assert.Equal("customer", reference.Name);
        Assert.Equal("customers", reference.TargetUnitId.Value);
        Assert.Equal(["customer_id"], reference.Columns);
    }

    [Fact]
    public void Empty_reference_collection_does_not_change_existing_canonical_documents()
    {
        var table = new SchemaTable(
            "customers",
            [new SchemaColumn("id", SchemaValueType.Guid, isNullable: false)],
            ["id"],
            references: []);

        var canonical = GroundworkSchemaCanonical.Serialize(new SchemaDocument([table]));

        Assert.DoesNotContain("references", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_schema_compilation_validates_deferred_reference_targets()
    {
        var target = new SchemaTable(
            "customers",
            [new SchemaColumn("id", SchemaValueType.Guid, isNullable: false)],
            ["id"]);
        var source = new SchemaTable(
            "orders",
            [
                new SchemaColumn("id", SchemaValueType.Guid, isNullable: false),
                new SchemaColumn("customer_id", SchemaValueType.Int64, isNullable: false)
            ],
            ["id"],
            indexes: [new SchemaIndex("by_customer", [new SchemaIndexColumn("customer_id")])],
            references: [new SchemaReference("customer", "customers", ["customer_id"])]);

        var exception = Assert.Throws<ArgumentException>(() =>
            SchemaCompilation.Compile(new SchemaDocument([source, target])));

        Assert.Contains("GW-DECL-REF-004", exception.Message, StringComparison.Ordinal);
    }
}
