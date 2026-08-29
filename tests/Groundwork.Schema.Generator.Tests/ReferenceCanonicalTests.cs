using Groundwork.Schema;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.SchemaTool;
using Xunit;

namespace Groundwork.Schema.Generator.Tests;

public sealed class ReferenceCanonicalTests
{
    [Fact]
    public void Reference_rejects_null_column_names_before_canonical_serialization()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new SchemaReference("customer", "customers", [null!]));

        Assert.Equal("columns", exception.ParamName);
    }

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
    public void Canonical_schema_round_trips_physical_references_and_checks()
    {
        var target = new SchemaTable(
            "customers",
            [new SchemaColumn("id", SchemaValueType.Guid, isNullable: false)],
            ["id"]);
        var source = new SchemaTable(
            "orders",
            [
                new SchemaColumn("id", SchemaValueType.Guid, isNullable: false),
                new SchemaColumn("customer_id", SchemaValueType.Guid, isNullable: false),
                new SchemaColumn("quantity", SchemaValueType.Int32, isNullable: false)
            ],
            ["id"],
            indexes: [new SchemaIndex("by_customer", [new SchemaIndexColumn("customer_id")])],
            references: [new SchemaReference("fk_orders_customer", "customers", ["customer_id"], physical: true)],
            checks: [new SchemaCheck(
                "ck_orders_quantity",
                "quantity",
                SchemaCheckOperator.GreaterThan,
                new SchemaDefault(0))]);

        var canonical = GroundworkSchemaCanonical.Serialize(new SchemaDocument([source, target]));
        var compiled = SchemaCompilation.Compile(GroundworkSchemaCanonical.Parse(canonical));

        Assert.Contains("\"physical\":true", canonical, StringComparison.Ordinal);
        Assert.Contains("\"checks\":[", canonical, StringComparison.Ordinal);
        var unit = compiled.Single(candidate => candidate.Id.Value == "orders");
        var reference = Assert.Single(unit.References);
        Assert.Equal(Groundwork.Kernel.ReferenceEnforcement.Physical, reference.Enforcement);
        Assert.Equal("customers", reference.TargetName);
        Assert.Equal(["id"], reference.TargetKeyColumns);
        Assert.Equal(0, Assert.Single(unit.CheckConstraints).Value.Value);
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

    [Fact]
    public void Physical_reference_targets_compile_before_their_sources()
    {
        var target = new SchemaTable(
            "z_customers",
            [new SchemaColumn("id", SchemaValueType.Guid, isNullable: false)],
            ["id"]);
        var source = new SchemaTable(
            "a_orders",
            [
                new SchemaColumn("id", SchemaValueType.Guid, isNullable: false),
                new SchemaColumn("customer_id", SchemaValueType.Guid, isNullable: false)
            ],
            ["id"],
            indexes: [new SchemaIndex("by_customer", [new SchemaIndexColumn("customer_id")])],
            references: [new SchemaReference("fk_orders_customer", "z_customers", ["customer_id"], physical: true)]);

        var targets = SchemaCompilation.CompileTargets(
            new SchemaDocument([source, target]),
            new TestTargetCompiler());

        Assert.Equal(["z_customers", "a_orders"], targets.Select(item => item.Subject.Id.Value));
    }

    private sealed class TestTargetCompiler : IPhysicalSchemaTargetCompiler
    {
        public PhysicalSchemaTarget Compile(StorageUnit declaration) =>
            new(new SchemaSubject(declaration), new ProviderIdentity("test", "1"));
    }
}
