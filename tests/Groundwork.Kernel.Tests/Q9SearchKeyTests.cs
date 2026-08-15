using Groundwork.Kernel;
using Xunit;

namespace Groundwork.Kernel.Tests;

public sealed class Q9SearchKeyTests
{
    [Fact]
    public void Folded_physical_expansion_adds_one_key_and_retargets_logical_indexes()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("tickets"),
            Name = "tickets",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new ColumnDefinition { Name = "status", Type = PortableType.String, MaxLength = 32, Collation = PortableCollation.OrdinalIgnoreCase },
                new ColumnDefinition { Name = "name", Type = PortableType.String, MaxLength = 32, Collation = PortableCollation.Ordinal }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Indexes = [new IndexDefinition { Name = "ix_status", Columns = [new IndexColumn("status")] }]
        };

        var physical = SearchKeyProjection.Expand(unit);

        Assert.Equal(["id", "status", "name", "__groundwork_search_status"], physical.Columns.Select(column => column.Name));
        Assert.DoesNotContain(physical.Columns, column => column.Name == "__groundwork_search_name");
        Assert.Equal("__groundwork_search_status", physical.Indexes.Single().Columns.Single().Column);
        var derived = Assert.Single(physical.DerivedColumns);
        Assert.Equal("status", derived.SourceColumn);
        Assert.Equal(PortableStringComparison.GetSearchKeyAlgorithmId(PortableStringComparisonPolicy.AsciiIgnoreCase), derived.AlgorithmId);
        Assert.Equal(PortableCollation.Ordinal, physical.Columns.Single(column => column.Name == "status").Collation);
        Assert.Equal(PortableCollation.OrdinalIgnoreCase, physical.Columns.Single(column => column.Name == "status").LogicalCollation);
    }

    [Theory]
    [InlineData("A", "|0061")]
    [InlineData("Turkish I", "|0074|0075|0072|006B|0069|0073|0068|0020|0069")]
    public void Ascii_fold_keys_are_boundary_delimited(string value, string expected)
    {
        Assert.Equal(expected, PortableStringComparison.CreateSearchKey(value, PortableStringComparisonPolicy.AsciiIgnoreCase));
    }

    [Fact]
    public void Search_key_successor_carries_hex_units_and_refuses_maximum_boundary()
    {
        Assert.Equal("|0042", PortableStringComparison.CreateSearchKeySuccessor("|0041"));
        Assert.Equal("|0050", PortableStringComparison.CreateSearchKeySuccessor("|004F"));
        Assert.Null(PortableStringComparison.CreateSearchKeySuccessor("|FFFF"));
    }

    [Fact]
    public void Populate_computes_hidden_key_and_rejects_provider_owned_input()
    {
        var unit = SearchKeyProjection.Expand(new StorageUnit
        {
            Id = new StorageUnitId("tickets"),
            Name = "tickets",
            Columns = [new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new ColumnDefinition { Name = "status", Type = PortableType.String, MaxLength = 32, Collation = PortableCollation.OrdinalIgnoreCase }],
            Key = new KeyDefinition { Columns = ["id"] }
        });

        var values = SearchKeyProjection.Populate(unit, new Dictionary<string, object?>
        {
            ["id"] = 1,
            ["status"] = "OPEN"
        });

        Assert.Equal("|006F|0070|0065|006E", values["__groundwork_search_status"]);
        Assert.DoesNotContain("__groundwork_search_status", SearchKeyProjection.Populate(unit, new Dictionary<string, object?>
        {
            ["id"] = 1
        }).Keys);
        Assert.Throws<ArgumentException>(() => SearchKeyProjection.Populate(unit, new Dictionary<string, object?>
        {
            ["id"] = 1,
            ["status"] = "OPEN",
            ["__groundwork_search_status"] = "spoofed"
        }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("stale-search-key-v0")]
    [InlineData("prefix-groundwork-ascii-lower-v1-suffix")]
    public void Populate_refuses_unknown_or_malformed_search_key_algorithm_ids(string? algorithmId)
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("malformed-search-key"),
            Name = "malformed_search_key",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new ColumnDefinition { Name = "status", Type = PortableType.String },
                new ColumnDefinition { Name = "__groundwork_search_status", Type = PortableType.String }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            DerivedColumns =
            [
                new DerivedColumnDefinition
                {
                    Name = "__groundwork_search_status",
                    SourceColumn = "status",
                    Projection = PortableProjection.BoundarySearchKey,
                    AlgorithmId = algorithmId
                }
            ]
        };

        var failure = Assert.Throws<InvalidOperationException>(() => SearchKeyProjection.Populate(
            unit,
            new Dictionary<string, object?> { ["id"] = 1, ["status"] = "Open" }));

        Assert.Contains("algorithm", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rebuild", failure.Message, StringComparison.OrdinalIgnoreCase);
    }
}
