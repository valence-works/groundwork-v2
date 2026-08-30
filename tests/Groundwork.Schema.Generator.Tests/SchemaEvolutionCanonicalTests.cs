using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Schema;
using Groundwork.SchemaTool;
using Xunit;

namespace Groundwork.Schema.Generator.Tests;

public sealed class SchemaEvolutionCanonicalTests
{
    [Fact]
    public void Default_evolution_keeps_the_pre_evolution_canonical_document_byte_identical()
    {
        var schema = new SchemaDocument(
        [
            new SchemaTable(
                "customers",
                [new SchemaColumn("id", SchemaValueType.Guid, isNullable: false)],
                ["id"],
                evolution: new SchemaEvolution())
        ]);

        var canonical = GroundworkSchemaCanonical.Serialize(schema);

        Assert.Equal(
            "{\"tables\":[{\"name\":\"customers\",\"columns\":[{\"name\":\"id\",\"type\":\"Guid\",\"nullable\":false," +
            "\"length\":null,\"precision\":null,\"scale\":null,\"folding\":\"None\",\"generation\":\"Supplied\",\"default\":null}]," +
            "\"key\":[\"id\"],\"indexes\":[],\"scope\":\"Global\",\"concurrency\":null,\"timestamps\":\"None\",\"retention\":null," +
            "\"appendIdempotency\":null,\"retentionIdempotency\":null,\"aggregations\":[]}]}",
            canonical);
    }

    [Fact]
    public void Canonical_evolution_round_trips_and_reaches_provider_compilation()
    {
        var schema = new SchemaDocument(
        [
            new SchemaTable(
                "retired",
                [new SchemaColumn("id", SchemaValueType.Int64, isNullable: false)],
                ["id"],
                evolution: new SchemaEvolution(isDestructive: true, retiresPrimaryStorage: true)),
            new SchemaTable(
                "tickets",
                [
                    new SchemaColumn("id", SchemaValueType.String, isNullable: false, length: 64),
                    new SchemaColumn("slug_v2", SchemaValueType.String, length: 128)
                ],
                ["id"],
                evolution: new SchemaEvolution(
                    semanticMigrationId: "2026-08-slugify",
                    supersessions:
                    [
                        new SchemaColumnSupersession(
                            new SchemaColumn("slug", SchemaValueType.String, length: 64),
                            "slug_v2")
                    ],
                    dualPresenceWindow: TimeSpan.FromHours(1)))
        ]);

        var canonical = GroundworkSchemaCanonical.Serialize(schema);
        var targets = SchemaCompilation.CompileTargets(
            GroundworkSchemaCanonical.Parse(canonical),
            new TestCompiler());

        Assert.Contains("\"evolution\":{", canonical, StringComparison.Ordinal);
        var retired = targets.Single(target => target.Subject.Id.Value == "retired").Subject.Evolution;
        Assert.True(retired.IsDestructive);
        Assert.True(retired.RetiresPrimaryStorage);
        var evolution = targets.Single(target => target.Subject.Id.Value == "tickets").Subject.Evolution;
        Assert.Equal("2026-08-slugify", evolution.SemanticMigrationId);
        Assert.Equal(TimeSpan.FromHours(1), evolution.DualPresenceWindow);
        var supersession = Assert.Single(evolution.Supersessions);
        Assert.Equal("slug", supersession.SupersededColumn.Name);
        Assert.Equal(PortableType.String, supersession.SupersededColumn.Type);
        Assert.Equal(64, supersession.SupersededColumn.MaxLength);
        Assert.Equal("slug_v2", supersession.ReplacementColumn);
    }

    [Fact]
    public void Authored_evolution_may_omit_default_members()
    {
        const string json =
            "{\"tables\":[{\"name\":\"retired\",\"columns\":[{\"name\":\"id\",\"type\":\"Int64\",\"nullable\":false}]," +
            "\"key\":[\"id\"],\"indexes\":[],\"evolution\":{\"retiresPrimaryStorage\":true}}]}";

        var table = Assert.Single(GroundworkSchemaCanonical.Parse(json).Tables);

        Assert.NotNull(table.Evolution);
        Assert.True(table.Evolution.RetiresPrimaryStorage);
        Assert.False(table.Evolution.IsDestructive);
        Assert.Empty(table.Evolution.Supersessions);
        Assert.Equal(TimeSpan.Zero, table.Evolution.DualPresenceWindow);
    }

    private sealed class TestCompiler : IPhysicalSchemaTargetCompiler
    {
        public PhysicalSchemaTarget Compile(StorageUnit declaration) =>
            new(new SchemaSubject(declaration), new ProviderIdentity("test", "1"));
    }
}
