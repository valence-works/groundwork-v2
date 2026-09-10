using Groundwork.Kernel;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteNativePlanMapperTests
{
    [Theory]
    [InlineData("[SCAN] records")]
    [InlineData("SEARCH records USING \"INDEX\" ix_records_status (status=?)")]
    [InlineData("SEARCH records USING INDEX ix_records_status ()")]
    [InlineData("SEARCH records USING INTEGER PRIMARY KEY (   )")]
    public void Unknown_grammar_cannot_be_normalized_into_a_known_access(string detail) =>
        Assert.Null(Map([new(2, 0, detail)], Identity(), _ => Identity(),
            new Dictionary<string, string> { ["ix_records_status"] = "status" }));

    [Fact]
    public void Quoted_operator_in_one_row_withholds_the_entire_forest() =>
        Assert.Null(Map([new(2, 0, "SCAN records"), new(3, 0, "\"USE\" TEMP B-TREE FOR ORDER BY")],
            Identity(), _ => Identity(), new Dictionary<string, string>()));

    [Fact]
    public void Maps_index_search_and_normalizes_ids_and_parents()
    {
        var targetId = Identity();
        var indexId = Identity();
        var forest = Map(
            [
                new(7, 0, "SEARCH records USING INDEX ix_records_status (status=?)"),
                new(19, 7, "USE TEMP B-TREE FOR ORDER BY")
            ],
            targetId,
            _ => indexId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ix_records_status"] = "status"
            });

        var nodes = Assert.IsType<ProviderPlanForest>(forest).Nodes;
        Assert.Equal(2, nodes.Length);
        Assert.Equal(0, nodes[0].Id);
        Assert.Null(nodes[0].ParentId);
        Assert.Equal(ProviderPlanOperator.IndexSearch, nodes[0].Operation);
        Assert.Equal(targetId, nodes[0].TargetId);
        Assert.Equal(indexId, nodes[0].IndexId);
        Assert.Equal("status", nodes[0].LogicalIndexName);
        Assert.False(nodes[0].IsCovering);
        Assert.Equal(1, nodes[1].Id);
        Assert.Equal(0, nodes[1].ParentId);
        Assert.Equal(ProviderPlanOperator.Sort, nodes[1].Operation);
        Assert.Equal(ProviderPlanSortPurpose.OrderBy, nodes[1].SortPurpose);
    }

    [Theory]
    [InlineData("SCAN records", ProviderPlanOperator.TableScan, null)]
    [InlineData("SCAN records USING INDEX ix_records_status", ProviderPlanOperator.IndexScan, false)]
    [InlineData("SCAN records USING COVERING INDEX ix_records_status", ProviderPlanOperator.IndexScan, true)]
    public void Maps_scan_variants_without_inventing_covering_for_table_scans(
        string detail, ProviderPlanOperator operation, bool? isCovering)
    {
        var targetId = Identity();
        var forest = Map(
            [new(3, 0, detail)],
            targetId,
            _ => Identity(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ix_records_status"] = "status"
            });

        var node = Assert.Single(Assert.IsType<ProviderPlanForest>(forest).Nodes);
        Assert.Equal(operation, node.Operation);
        Assert.Equal(targetId, node.TargetId);
        Assert.Equal(isCovering, node.IsCovering);
        if (operation == ProviderPlanOperator.TableScan)
        {
            Assert.Null(node.IndexId);
            Assert.Null(node.LogicalIndexName);
        }
        else
        {
            Assert.NotNull(node.IndexId);
            Assert.Equal("status", node.LogicalIndexName);
        }
    }

    /// <summary>#423: the automatic index behind a composite PRIMARY KEY is the key search, not an index identity.</summary>
    [Fact]
    public void Maps_automatic_primary_key_index_search_as_a_primary_key_search()
    {
        var target = new ProviderOpaqueIdentity(Guid.NewGuid());
        var forest = Map([new(3, 0, "SEARCH records USING INDEX sqlite_autoindex_records_1 (__groundwork_scope=? AND id=?)")],
            target, _ => new ProviderOpaqueIdentity(Guid.NewGuid()), new Dictionary<string, string>());

        var node = Assert.Single(Assert.IsType<ProviderPlanForest>(forest).Nodes);
        Assert.Equal(ProviderPlanOperator.PrimaryKeySearch, node.Operation);
        Assert.Equal(target, node.TargetId);
        Assert.Null(node.IndexId);
        Assert.Null(node.LogicalIndexName);
        Assert.Null(Map([new(3, 0, "SEARCH records USING INDEX sqlite_autoindex_other_1 (id=?)")],
            target, _ => new ProviderOpaqueIdentity(Guid.NewGuid()), new Dictionary<string, string>()));
    }

    [Fact]
    public void Maps_integer_primary_key_search_without_an_index_identity()
    {
        var targetId = Identity();
        var forest = Map(
            [new(4, 0, "SEARCH records USING INTEGER PRIMARY KEY (rowid=?)")],
            targetId,
            _ => throw new InvalidOperationException("Primary-key searches must not resolve an index."),
            new Dictionary<string, string>(StringComparer.Ordinal));

        var node = Assert.Single(Assert.IsType<ProviderPlanForest>(forest).Nodes);
        Assert.Equal(ProviderPlanOperator.PrimaryKeySearch, node.Operation);
        Assert.Equal(targetId, node.TargetId);
        Assert.Null(node.IndexId);
        Assert.Null(node.IsCovering);
    }

    [Fact]
    public void Preserves_sort_sibling_roots()
    {
        var forest = Map(
            [
                new(2, 0, "SEARCH records USING INDEX ix_records_status (status=?)"),
                new(5, 0, "USE TEMP B-TREE FOR DISTINCT")
            ],
            Identity(),
            _ => Identity(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ix_records_status"] = "status"
            });

        var nodes = Assert.IsType<ProviderPlanForest>(forest).Nodes;
        Assert.All(nodes, node => Assert.Null(node.ParentId));
        Assert.Equal(ProviderPlanOperator.IndexSearch, nodes[0].Operation);
        Assert.Equal(ProviderPlanOperator.Sort, nodes[1].Operation);
        Assert.Equal(ProviderPlanSortPurpose.Distinct, nodes[1].SortPurpose);
    }

    [Fact]
    public void Does_not_match_a_target_prefix()
    {
        var forest = Map(
            [new(2, 0, "SCAN records_archive")],
            Identity(),
            _ => Identity(),
            new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Null(forest);
    }

    [Fact]
    public void Withholds_an_index_prefix_instead_of_matching_the_declared_index()
    {
        var forest = Map(
            [new(2, 0, "SEARCH records USING INDEX ix_records_status_archive (status=?)")],
            Identity(),
            _ => Identity(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ix_records_status"] = "status"
            });

        Assert.Null(forest);
    }

    [Fact]
    public void Withholds_the_entire_forest_when_one_row_is_unknown()
    {
        var forest = Map(
            [
                new(2, 0, "SEARCH records USING INDEX ix_records_status (status=?)"),
                new(5, 0, "SCAN records USING AUTOMATIC INDEX")
            ],
            Identity(),
            _ => Identity(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ix_records_status"] = "status"
            });

        Assert.Null(forest);
    }

    [Fact]
    public void Withholds_the_entire_forest_for_multiple_source_access_nodes()
    {
        var forest = Map(
            [
                new(2, 0, "SCAN records"),
                new(5, 0, "SEARCH records USING INTEGER PRIMARY KEY (rowid=?)")
            ],
            Identity(),
            _ => Identity(),
            new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Null(forest);
    }

    [Fact]
    public void Withholds_malformed_graphs_and_id_zero_ambiguity()
    {
        var cases = new IReadOnlyList<SqliteNativePlanRow>[]
        {
            [new(0, 0, "SCAN records")],
            [new(2, 0, "SCAN records"), new(2, 0, "SCAN records")],
            [new(2, 9, "SCAN records")],
            [new(2, 3, "SCAN records"), new(3, 2, "USE TEMP B-TREE FOR ORDER BY")]
        };

        foreach (var rows in cases)
        {
            Assert.Null(Map(
                rows,
                Identity(),
                _ => Identity(),
                new Dictionary<string, string>(StringComparer.Ordinal)));
        }
    }

    [Fact]
    public void Rejects_blank_or_unexpected_sort_details()
    {
        var details = new[]
        {
            "",
            "USE TEMP B-TREE FOR WINDOW",
            "MATERIALIZE records"
        };

        foreach (var detail in details)
        {
            Assert.Null(Map(
                [new(2, 0, detail)],
                Identity(),
                _ => Identity(),
                new Dictionary<string, string>(StringComparer.Ordinal)));
        }
    }

    private static ProviderPlanForest? Map(
        IReadOnlyList<SqliteNativePlanRow> rows,
        ProviderOpaqueIdentity targetId,
        Func<string, ProviderOpaqueIdentity> indexIdentity,
        IReadOnlyDictionary<string, string> logicalIndexesByPhysicalName) =>
        SqliteNativePlanMapper.Map(
            rows,
            "records",
            targetId,
            indexIdentity,
            logicalIndexesByPhysicalName);

    private static ProviderOpaqueIdentity Identity() => new(Guid.NewGuid());
}
