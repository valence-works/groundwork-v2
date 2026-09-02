using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Store;
using Xunit;

namespace Groundwork.Substrate.Relational.Tests;

public sealed class RelationalSessionPolicyTests
{
    [Fact]
    public void Scoped_query_preparation_binds_scope_and_completes_provider_options()
    {
        var unit = Unit(scoped: true);
        var request = Request(unit, Projection.All);

        var prepared = RelationalSessionPolicy.PrepareQuery(
            unit,
            StorageAccess.Scoped(new StorageScope("tenant-a")),
            request,
            options: null,
            new Dictionary<string, string> { ["ix_value"] = "physical_ix_value" });

        var predicate = Assert.IsType<Predicate.Equal>(prepared.ExecutionSource.Where);
        Assert.Equal(ProviderOwnedColumns.Scope, predicate.Column.Name);
        Assert.Equal("tenant-a", predicate.Value.Value);
        Assert.Equal("physical_ix_value", prepared.RenderOptions.PhysicalIndexNames["ix_value"]);
        Assert.DoesNotContain(
            prepared.RenderOptions.TieBreakColumns,
            column => column.Name == ProviderOwnedColumns.Scope);
    }

    [Fact]
    public void Cross_scope_preparation_adds_scope_projection_and_partition()
    {
        var unit = Unit(scoped: true);
        var id = new ColumnRef(new TableId(unit.Name), "id", QueryType.String, isNullable: false);
        var prepared = RelationalSessionPolicy.PrepareCrossScopeQuery(
            unit,
            StorageAccess.PrivilegedAcrossScopes(new StorageAccessAudit("operator", "repair")),
            Request(unit, Projection.ColumnsOnly([id])),
            options: null,
            new Dictionary<string, string>());

        Assert.Contains(
            prepared.ExecutionRequest.Projection.Columns,
            column => column.Name == ProviderOwnedColumns.Scope);
        var partition = Assert.Single(prepared.RenderOptions.LatestPartitionColumns);
        Assert.Equal(CrossScopeQueryMaterializer.ScopeTokenColumn, partition.Name);
    }

    [Fact]
    public void Write_value_validation_preserves_provider_diagnostics()
    {
        var unit = Unit(scoped: false) with
        {
            Columns =
            [
                new ColumnDefinition
                {
                    Name = "sequence",
                    Type = PortableType.Int64,
                    IsNullable = false,
                    Generation = ColumnGeneration.ProviderSequence
                },
                new ColumnDefinition { Name = "value", Type = PortableType.String, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["sequence"] }
        };

        var generated = Assert.Throws<ArgumentException>(() => RelationalSessionPolicy.ValidateValues(
            unit,
            unit.Columns,
            "StubDB",
            new Dictionary<string, object?> { ["sequence"] = 1L, ["value"] = "x" },
            requireAllNonNullable: true));
        Assert.Contains("assigned by StubDB", generated.Message, StringComparison.Ordinal);

        var required = Assert.Throws<ArgumentException>(() => RelationalSessionPolicy.ValidateValues(
            unit,
            unit.Columns,
            "StubDB",
            new Dictionary<string, object?>(),
            requireAllNonNullable: true));
        Assert.Contains("Non-nullable column 'value' is required", required.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Query_session_uses_the_schema_ordinal_identity_mapping_over_caller_options()
    {
        var logical = StorageUnit.Declare("policy-ordinal", "policy_ordinal")
            .Int32("id", column => column.Required())
            .String("name", 32, column => column.Required().OrdinalIdentity("__groundwork_ordinal_name"))
            .Key("id")
            .Index("by_name", index => index.UseOrdinalIdentities().Column("name"))
            .Index("by_name_order", index => index.Column("name").Column("id"))
            .Build();
        var unit = SearchKeyProjection.Expand(logical);
        var name = new ColumnRef(new TableId(unit.Name), "name", QueryType.String, false, 32);
        var options = new QueryRenderOptions(
            [new QueryIndexDeclaration("by_name", ["name"])],
            selectedIndex: "by_name") with
        {
            SearchKeyColumns = new Dictionary<string, QuerySearchKeyColumn>
            {
                ["name"] = new("name", "spoofed_identity", QuerySearchKeyPolicy.Ordinal, 32,
                    orderByPhysicalColumn: true, supportsPrefixPredicates: false,
                    preservesOrdinalIdentity: true)
            }
        };

        var prepared = RelationalSessionPolicy.PrepareQuery(
            unit,
            StorageAccess.Global,
            new QueryRequest(new TableId(unit.Name), Predicate.AlwaysTrue.Instance, [], Projection.ColumnsOnly(name), Paging.None),
            options,
            new Dictionary<string, string>());

        var mapping = Assert.Single(prepared.RenderOptions.SearchKeyColumns).Value;
        Assert.Equal("__groundwork_ordinal_name", mapping.PhysicalColumn);
        Assert.True(mapping.PreservesOrdinalIdentity);
        Assert.Equal(QuerySearchKeyPolicy.Ordinal, mapping.Policy);
        Assert.True(
            new[] { "__groundwork_ordinal_name" }.SequenceEqual(
                prepared.RenderOptions.Indexes.Single(index => index.Name == "by_name").Columns));

        var ordinaryOptions = new QueryRenderOptions(
            [new QueryIndexDeclaration("by_name_order", ["name", "id"])],
            selectedIndex: "by_name_order");
        var ordinary = RelationalSessionPolicy.PrepareQuery(
            unit,
            StorageAccess.Global,
            new QueryRequest(new TableId(unit.Name), Predicate.AlwaysTrue.Instance, [], Projection.ColumnsOnly(name), Paging.None),
            ordinaryOptions,
            new Dictionary<string, string>());

        var ordinaryMapping = ordinary.RenderOptions.SearchKeyColumns["name"];
        Assert.Equal("name", ordinaryMapping.PhysicalColumn);
        Assert.False(ordinaryMapping.PreservesOrdinalIdentity);
        Assert.True(new[] { "name", "id" }.SequenceEqual(ordinary.RenderOptions.Indexes.Single().Columns));
    }

    [Fact]
    public void Secondary_unique_detection_distinguishes_the_primary_key()
    {
        var unit = Unit(scoped: false);
        Assert.True(RelationalSessionPolicy.HasSecondaryUniqueIndex(unit));

        var primaryOnly = unit with
        {
            Indexes =
            [
                new IndexDefinition
                {
                    Name = "ix_primary",
                    Columns = [new IndexColumn("id")],
                    IsUnique = true
                }
            ]
        };
        Assert.False(RelationalSessionPolicy.HasSecondaryUniqueIndex(primaryOnly));
    }

    private static QueryRequest Request(StorageUnit unit, Projection projection) => new(
        new TableId(unit.Name),
        Predicate.AlwaysTrue.Instance,
        [],
        projection,
        Paging.OffsetLimit(0, 10));

    private static StorageUnit Unit(bool scoped) => new()
    {
        Id = new StorageUnitId("policy-unit"),
        Name = "policy_unit",
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false },
            new ColumnDefinition { Name = "value", Type = PortableType.String, IsNullable = true },
            new ColumnDefinition { Name = ProviderOwnedColumns.Scope, Type = PortableType.String, IsNullable = false }
        ],
        Key = new KeyDefinition
        {
            Columns = scoped ? [ProviderOwnedColumns.Scope, "id"] : ["id"]
        },
        Indexes =
        [
            new IndexDefinition
            {
                Name = "ix_value",
                Columns = [new IndexColumn("value")],
                IsUnique = true
            }
        ],
        Scope = scoped ? ScopePolicy.Scoped : ScopePolicy.Global
    };
}
