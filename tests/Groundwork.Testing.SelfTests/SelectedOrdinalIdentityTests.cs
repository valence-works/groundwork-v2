using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Store;

namespace Groundwork.Testing.SelfTests;

public sealed class SelectedOrdinalIdentityTests
{
    [Fact]
    public void Selected_ordinal_identity_mapping_requires_the_marker_and_matching_hidden_index_term()
    {
        var logical = StorageUnit.Declare("selected-ordinal-identity", "selected_ordinal_identity")
            .Int32("id", column => column.Required())
            .String("name", 64, column => column.Required().OrdinalIdentity("__groundwork_ordinal_name"))
            .Key("id")
            .Index("by_name", index => index.UseOrdinalIdentities().Column("name"))
            .Build();
        var physical = SearchKeyProjection.Expand(logical);
        var physicalIndex = Assert.Single(physical.Indexes);

        var exact = SearchKeyQueryMappings.For(physical, physicalIndex.Name)["name"];
        Assert.Equal("__groundwork_ordinal_name", exact.PhysicalColumn);
        Assert.True(exact.PreservesOrdinalIdentity);
        var selectedOptions = logical.CreateQueryRenderOptions(physicalIndex.Name);
        var queryIndex = Assert.Single(SearchKeyQueryMappings.RetargetIndexes(physical, selectedOptions.Indexes));
        Assert.True(new[] { "__groundwork_ordinal_name" }.SequenceEqual(queryIndex.Columns));
        Assert.True(new[] { "name" }.SequenceEqual(physicalIndex.IncludedColumns ?? []));

        var unmarked = physical with
        {
            Indexes = [physicalIndex with { UseOrdinalIdentities = false }]
        };
        var unmarkedMapping = SearchKeyQueryMappings.For(unmarked, physicalIndex.Name)["name"];
        Assert.Equal("name", unmarkedMapping.PhysicalColumn);
        Assert.False(unmarkedMapping.PreservesOrdinalIdentity);

        var missingHiddenTerm = physical with
        {
            Indexes = [physicalIndex with { Columns = [new IndexColumn("name")] }]
        };
        var missingTermMapping = SearchKeyQueryMappings.For(missingHiddenTerm, physicalIndex.Name)["name"];
        Assert.Equal("name", missingTermMapping.PhysicalColumn);
        Assert.False(missingTermMapping.PreservesOrdinalIdentity);
    }
}
