using Groundwork.Kernel;
using Groundwork.Query.Model;
using Xunit;

namespace Groundwork.SqlServer.Tests;

/// <summary>#421: observed order columns, literal bounds and replayed spill facts on SQL Server plans.</summary>
public sealed class SqlServerNativePlanDetailTests
{
    private const string Namespace = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    [Fact]
    public void Top_sort_exposes_order_columns_its_row_bound_and_a_replayed_no_spill()
    {
        var forest = Map(Plan(
            "<StmtSimple StatementType=\"SELECT\"><QueryPlan>" +
            "<RelOp NodeId=\"0\" PhysicalOp=\"Sort\"><RunTimeInformation/><TopSort Rows=\"11\">" +
            "<OrderBy><OrderByColumn Ascending=\"0\"><ColumnReference Schema=\"[dbo]\" Table=\"[records]\" Column=\"lastSeen\" /></OrderByColumn>" +
            "<OrderByColumn Ascending=\"1\"><ColumnReference Schema=\"[dbo]\" Table=\"[records]\" Column=\"id\" /></OrderByColumn></OrderBy>" +
            Scan(1) + "</TopSort></RelOp></QueryPlan></StmtSimple>"));

        var sort = Assert.Single(Assert.IsType<ProviderPlanForest>(forest).Nodes, node => node.Operation == ProviderPlanOperator.TopNSort);
        var details = Assert.IsType<ProviderPlanNodeDetails>(sort.Details);
        Assert.Collection(details.NativeSortKeys!.Value,
            term => { Assert.Equal("lastSeen", term.LogicalColumn); Assert.Equal(OrderDirection.Descending, term.Direction); Assert.Empty(term.Transforms); },
            term => { Assert.Equal("id", term.LogicalColumn); Assert.Equal(OrderDirection.Ascending, term.Direction); });
        Assert.Equal(ProviderNativeBoundKind.Explicit, details.NativeLimit.Kind);
        Assert.Equal(11, details.NativeLimit.Value);
        Assert.False(Assert.IsType<ProviderPlanSpillDetail>(details.Spill).Spilled);
    }

    [Fact]
    public void Datalength_order_expression_is_the_ordinal_string_key_transform_on_its_column()
    {
        var forest = Map(Plan(
            "<StmtSimple StatementType=\"SELECT\"><QueryPlan>" +
            "<RelOp NodeId=\"0\" PhysicalOp=\"Sort\"><Sort><OrderBy>" +
            "<OrderByColumn Ascending=\"1\"><ColumnReference Column=\"Expr1003\" /></OrderByColumn>" +
            "<OrderByColumn Ascending=\"1\"><ColumnReference Schema=\"[dbo]\" Table=\"[records]\" Column=\"id\" /></OrderByColumn></OrderBy>" +
            "<RelOp NodeId=\"1\" PhysicalOp=\"Compute Scalar\"><ComputeScalar><DefinedValues>" + Datalength("Expr1003", "id") + "</DefinedValues>" +
            Scan(2) + "</ComputeScalar></RelOp></Sort></RelOp></QueryPlan></StmtSimple>"));

        var sort = Assert.Single(Assert.IsType<ProviderPlanForest>(forest).Nodes, node => node.Operation == ProviderPlanOperator.Sort);
        var keys = sort.Details!.NativeSortKeys!.Value;
        Assert.Equal(2, keys.Length);
        Assert.Equal("id", keys[0].LogicalColumn);
        Assert.Equal(ProviderOrderingTransform.OrdinalStringKey, Assert.Single(keys[0].Transforms));
        Assert.Equal(ProviderPredicateComparison.Unknown, keys[0].Comparison);
        Assert.Equal("id", keys[1].LogicalColumn);
        Assert.Empty(keys[1].Transforms);
        Assert.Equal(ProviderPredicateComparison.Unknown, keys[1].Comparison);
        Assert.Null(sort.Details.Spill);
        Assert.Equal(ProviderNativeBoundKind.Unknown, sort.Details.NativeLimit.Kind);
    }

    /// <summary>
    /// The renderer's ordinal string key is the collated column and then its datalength; SQL Server
    /// keeps that pair for a non-unique column and drops the length key behind the unique one.
    /// </summary>
    [Fact]
    public void Collated_column_and_its_datalength_fold_into_one_ordinal_term_and_a_unique_tail_stays_ordinal()
    {
        var forest = Map(Plan(
            "<StmtSimple StatementType=\"SELECT\"><QueryPlan>" +
            "<RelOp NodeId=\"0\" PhysicalOp=\"Sort\"><Sort><OrderBy>" +
            "<OrderByColumn Ascending=\"0\"><ColumnReference Schema=\"[dbo]\" Table=\"[records]\" Column=\"lastSeen\" /></OrderByColumn>" +
            "<OrderByColumn Ascending=\"1\"><ColumnReference Schema=\"[dbo]\" Table=\"[records]\" Column=\"idOrderKey\" /></OrderByColumn>" +
            "<OrderByColumn Ascending=\"1\"><ColumnReference Column=\"Expr1003\" /></OrderByColumn>" +
            "<OrderByColumn Ascending=\"1\"><ColumnReference Schema=\"[dbo]\" Table=\"[records]\" Column=\"id\" /></OrderByColumn></OrderBy>" +
            "<RelOp NodeId=\"1\" PhysicalOp=\"Compute Scalar\"><ComputeScalar><DefinedValues>" +
            Datalength("Expr1003", "idOrderKey") + Datalength("Expr1004", "id") + "</DefinedValues>" +
            Scan(2) + "</ComputeScalar></RelOp></Sort></RelOp></QueryPlan></StmtSimple>"),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["idOrderKey"] = "idOrderKey", ["id"] = "id" });

        var sort = Assert.Single(Assert.IsType<ProviderPlanForest>(forest).Nodes, node => node.Operation == ProviderPlanOperator.Sort);
        Assert.Collection(sort.Details!.NativeSortKeys!.Value,
            term =>
            {
                Assert.Equal("lastSeen", term.LogicalColumn);
                Assert.Equal(OrderDirection.Descending, term.Direction);
                Assert.Empty(term.Transforms);
                Assert.Equal(ProviderPredicateComparison.Unknown, term.Comparison);
            },
            term =>
            {
                Assert.Equal("idOrderKey", term.LogicalColumn);
                Assert.Equal(OrderDirection.Ascending, term.Direction);
                Assert.Equal(ProviderOrderingTransform.OrdinalStringKey, Assert.Single(term.Transforms));
                Assert.Equal(ProviderPredicateComparison.Ordinal, term.Comparison);
            },
            term =>
            {
                Assert.Equal("id", term.LogicalColumn);
                Assert.Equal(OrderDirection.Ascending, term.Direction);
                Assert.Empty(term.Transforms);
                Assert.Equal(ProviderPredicateComparison.Ordinal, term.Comparison);
            });
    }

    [Fact]
    public void Datalength_in_the_other_direction_does_not_fold_into_its_column()
    {
        var forest = Map(Plan(
            "<StmtSimple StatementType=\"SELECT\"><QueryPlan>" +
            "<RelOp NodeId=\"0\" PhysicalOp=\"Sort\"><Sort><OrderBy>" +
            "<OrderByColumn Ascending=\"1\"><ColumnReference Schema=\"[dbo]\" Table=\"[records]\" Column=\"id\" /></OrderByColumn>" +
            "<OrderByColumn Ascending=\"0\"><ColumnReference Column=\"Expr1003\" /></OrderByColumn></OrderBy>" +
            "<RelOp NodeId=\"1\" PhysicalOp=\"Compute Scalar\"><ComputeScalar><DefinedValues>" + Datalength("Expr1003", "id") + "</DefinedValues>" +
            Scan(2) + "</ComputeScalar></RelOp></Sort></RelOp></QueryPlan></StmtSimple>"),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["id"] = "id" });

        var sort = Assert.Single(Assert.IsType<ProviderPlanForest>(forest).Nodes, node => node.Operation == ProviderPlanOperator.Sort);
        var keys = sort.Details!.NativeSortKeys!.Value;
        Assert.Equal(2, keys.Length);
        Assert.Empty(keys[0].Transforms);
        Assert.Equal(ProviderPredicateComparison.Ordinal, keys[0].Comparison);
        Assert.Equal(OrderDirection.Descending, keys[1].Direction);
        Assert.Equal(ProviderOrderingTransform.OrdinalStringKey, Assert.Single(keys[1].Transforms));
    }

    [Fact]
    public void Ordinal_identity_search_key_column_orders_its_logical_column_through_the_physical_search_key()
    {
        var forest = Map(Plan(
            "<StmtSimple StatementType=\"SELECT\"><QueryPlan>" +
            "<RelOp NodeId=\"0\" PhysicalOp=\"Sort\"><Sort><OrderBy>" +
            "<OrderByColumn Ascending=\"1\"><ColumnReference Schema=\"[dbo]\" Table=\"[records]\" Column=\"__groundwork_sk_id\" /></OrderByColumn></OrderBy>" +
            Scan(1) + "</Sort></RelOp></QueryPlan></StmtSimple>"),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["__groundwork_sk_id"] = "id" });

        var sort = Assert.Single(Assert.IsType<ProviderPlanForest>(forest).Nodes, node => node.Operation == ProviderPlanOperator.Sort);
        var term = Assert.Single(sort.Details!.NativeSortKeys!.Value);
        Assert.Equal("id", term.LogicalColumn);
        Assert.Equal(ProviderOrderingTransform.PhysicalSearchKey, Assert.Single(term.Transforms));
        Assert.Equal(ProviderPredicateComparison.Ordinal, term.Comparison);
    }

    [Fact]
    public void Spill_warning_and_top_constant_are_observed_and_foreign_order_columns_are_not()
    {
        var forest = Map(Plan(
            "<StmtSimple StatementType=\"SELECT\"><QueryPlan>" +
            "<RelOp NodeId=\"0\" PhysicalOp=\"Top\"><Top RowCount=\"0\"><TopExpression><ScalarOperator><Const ConstValue=\"(31)\" /></ScalarOperator></TopExpression>" +
            "<RelOp NodeId=\"1\" PhysicalOp=\"Sort\"><Warnings><SpillToTempDb SpillLevel=\"1\" SpilledThreadCount=\"1\" /></Warnings><RunTimeInformation/><Sort><OrderBy>" +
            "<OrderByColumn Ascending=\"1\"><ColumnReference Schema=\"[dbo]\" Table=\"[other]\" Column=\"id\" /></OrderByColumn></OrderBy>" +
            Scan(2) + "</Sort></RelOp></Top></RelOp></QueryPlan></StmtSimple>"));

        var nodes = Assert.IsType<ProviderPlanForest>(forest).Nodes;
        var top = Assert.Single(nodes, node => node.Operation == ProviderPlanOperator.Limit);
        Assert.Equal(31, top.Details!.NativeLimit.Value);
        var sort = Assert.Single(nodes, node => node.Operation == ProviderPlanOperator.Sort);
        Assert.Null(sort.Details!.NativeSortKeys);
        Assert.True(sort.Details.Spill!.Spilled);
    }

    [Fact]
    public void Estimated_plan_without_runtime_information_leaves_spill_unobserved_and_expression_bounds_unknown()
    {
        var forest = Map(Plan(
            "<StmtSimple StatementType=\"SELECT\"><QueryPlan>" +
            "<RelOp NodeId=\"0\" PhysicalOp=\"Top\"><Top RowCount=\"0\"><TopExpression><ScalarOperator><Identifier><ColumnReference Column=\"ConstExpr1007\" /></Identifier></ScalarOperator></TopExpression>" +
            "<RelOp NodeId=\"1\" PhysicalOp=\"Sort\"><Sort><OrderBy><OrderByColumn Ascending=\"1\"><ColumnReference Schema=\"[dbo]\" Table=\"[records]\" Column=\"id\" /></OrderByColumn></OrderBy>" +
            Scan(2) + "</Sort></RelOp></Top></RelOp></QueryPlan></StmtSimple>"));

        var nodes = Assert.IsType<ProviderPlanForest>(forest).Nodes;
        Assert.Null(Assert.Single(nodes, node => node.Operation == ProviderPlanOperator.Limit).Details);
        var sort = Assert.Single(nodes, node => node.Operation == ProviderPlanOperator.Sort);
        Assert.Null(sort.Details!.Spill);
        Assert.Equal("id", Assert.Single(sort.Details.NativeSortKeys!.Value).LogicalColumn);
    }

    [Fact]
    public void Null_rank_case_expression_is_the_null_rank_transform_on_its_column()
    {
        var forest = Map(Plan(
            "<StmtSimple StatementType=\"SELECT\"><QueryPlan>" +
            "<RelOp NodeId=\"0\" PhysicalOp=\"Sort\"><Sort><OrderBy>" +
            "<OrderByColumn Ascending=\"1\"><ColumnReference Column=\"Expr1004\" /></OrderByColumn>" +
            "<OrderByColumn Ascending=\"0\"><ColumnReference Schema=\"[dbo]\" Table=\"[records]\" Column=\"lastSeen\" /></OrderByColumn></OrderBy>" +
            "<RelOp NodeId=\"1\" PhysicalOp=\"Compute Scalar\"><ComputeScalar><DefinedValues><DefinedValue><ColumnReference Column=\"Expr1004\" />" +
            "<ScalarOperator><IF><Condition><ScalarOperator><Compare CompareOp=\"IS\"><ScalarOperator><Identifier><ColumnReference Schema=\"[dbo]\" Table=\"[records]\" Column=\"lastSeen\" /></Identifier></ScalarOperator>" +
            "<ScalarOperator><Const ConstValue=\"NULL\" /></ScalarOperator></Compare></ScalarOperator></Condition>" +
            "<Then><ScalarOperator><Const ConstValue=\"(1)\" /></ScalarOperator></Then><Else><ScalarOperator><Const ConstValue=\"(0)\" /></ScalarOperator></Else></IF></ScalarOperator>" +
            "</DefinedValue></DefinedValues>" + Scan(2) + "</ComputeScalar></RelOp></Sort></RelOp></QueryPlan></StmtSimple>"));

        var sort = Assert.Single(Assert.IsType<ProviderPlanForest>(forest).Nodes, node => node.Operation == ProviderPlanOperator.Sort);
        var keys = sort.Details!.NativeSortKeys!.Value;
        Assert.Equal("lastSeen", keys[0].LogicalColumn);
        Assert.Equal(ProviderOrderingTransform.NullRank, Assert.Single(keys[0].Transforms));
        Assert.Equal("lastSeen", keys[1].LogicalColumn);
        Assert.Equal(OrderDirection.Descending, keys[1].Direction);
    }

    /// <summary>#448: the seek-plus-lookup shape of a non-covering index seek on a heap.</summary>
    [Fact]
    public void Nested_loops_over_a_seek_and_its_rid_lookup_materializes_the_seek_without_a_second_source()
    {
        var forest = Map(Plan(
            "<StmtSimple StatementType=\"SELECT\"><QueryPlan>" +
            "<RelOp NodeId=\"0\" PhysicalOp=\"Top\"><Top RowCount=\"0\"><TopExpression><ScalarOperator><Identifier><ColumnReference Column=\"@p1\" /></Identifier></ScalarOperator></TopExpression>" +
            "<RelOp NodeId=\"2\" PhysicalOp=\"Nested Loops\" LogicalOp=\"Inner Join\"><NestedLoops Optimized=\"0\" WithOrderedPrefetch=\"1\">" +
            "<RelOp NodeId=\"4\" PhysicalOp=\"Filter\"><Filter StartupExpression=\"0\">" +
            "<RelOp NodeId=\"5\" PhysicalOp=\"Index Seek\"><IndexScan Ordered=\"1\"><Object Database=\"[records_db]\" Schema=\"[dbo]\" Table=\"[records]\" Index=\"[PK_records]\" IndexKind=\"NonClustered\" /></IndexScan></RelOp>" +
            "</Filter></RelOp>" +
            Lookup(7, "RID Lookup", "<Object Database=\"[records_db]\" Schema=\"[dbo]\" Table=\"[records]\" TableReferenceId=\"-1\" IndexKind=\"Heap\" />") +
            "</NestedLoops></RelOp></Top></RelOp></QueryPlan></StmtSimple>"));

        var nodes = Assert.IsType<ProviderPlanForest>(forest).Nodes;
        Assert.Collection(nodes,
            node => Assert.Equal(ProviderPlanOperator.Limit, node.Operation),
            node => { Assert.Equal(ProviderPlanOperator.Materialize, node.Operation); Assert.Equal(0, node.ParentId); Assert.Null(node.TargetId); },
            node => { Assert.Equal(ProviderPlanOperator.Filter, node.Operation); Assert.Equal(2, node.ParentId); },
            node => { Assert.Equal(ProviderPlanOperator.IndexSearch, node.Operation); Assert.Equal(4, node.ParentId); },
            node => { Assert.Equal(ProviderPlanOperator.Materialize, node.Operation); Assert.Equal(7, node.Id); Assert.Equal(2, node.ParentId); Assert.Null(node.TargetId); });
        Assert.Single(nodes, node => node.TargetId is not null);
    }

    [Fact]
    public void Key_lookup_on_the_clustered_index_is_the_same_materialization()
    {
        var forest = Map(Plan(
            "<StmtSimple StatementType=\"SELECT\"><QueryPlan>" +
            "<RelOp NodeId=\"0\" PhysicalOp=\"Nested Loops\"><NestedLoops>" +
            "<RelOp NodeId=\"1\" PhysicalOp=\"Index Seek\"><IndexScan><Object Database=\"[records_db]\" Schema=\"[dbo]\" Table=\"[records]\" Index=\"[PK_records]\" /></IndexScan></RelOp>" +
            Lookup(2, "Key Lookup", "<Object Database=\"[records_db]\" Schema=\"[dbo]\" Table=\"[records]\" Index=\"[PK_records]\" IndexKind=\"Clustered\" />") +
            "</NestedLoops></RelOp></QueryPlan></StmtSimple>"));

        var nodes = Assert.IsType<ProviderPlanForest>(forest).Nodes;
        Assert.Equal([ProviderPlanOperator.Materialize, ProviderPlanOperator.IndexSearch, ProviderPlanOperator.Materialize], nodes.Select(node => node.Operation));
        Assert.Single(nodes, node => node.TargetId is not null);
    }

    [Theory]
    [InlineData("<RelOp NodeId=\"2\" PhysicalOp=\"Index Seek\"><IndexScan><Object Database=\"[records_db]\" Schema=\"[dbo]\" Table=\"[records]\" Index=\"[PK_records]\" /></IndexScan></RelOp>")]
    [InlineData("<RelOp NodeId=\"2\" PhysicalOp=\"RID Lookup\"><IndexScan Lookup=\"1\"><Object Database=\"[records_db]\" Schema=\"[dbo]\" Table=\"[other]\" IndexKind=\"Heap\" /></IndexScan></RelOp>")]
    [InlineData("<RelOp NodeId=\"2\" PhysicalOp=\"RID Lookup\"><IndexScan><Object Database=\"[records_db]\" Schema=\"[dbo]\" Table=\"[records]\" IndexKind=\"Heap\" /></IndexScan></RelOp>")]
    public void A_join_whose_inner_side_is_not_a_lookup_of_the_target_withholds_the_forest(string inner)
    {
        var forest = Map(Plan(
            "<StmtSimple StatementType=\"SELECT\"><QueryPlan>" +
            "<RelOp NodeId=\"0\" PhysicalOp=\"Nested Loops\"><NestedLoops>" +
            "<RelOp NodeId=\"1\" PhysicalOp=\"Index Seek\"><IndexScan><Object Database=\"[records_db]\" Schema=\"[dbo]\" Table=\"[records]\" Index=\"[PK_records]\" /></IndexScan></RelOp>" +
            inner + "</NestedLoops></RelOp></QueryPlan></StmtSimple>"));

        Assert.Null(forest);
    }

    [Fact]
    public void A_lookup_outside_its_join_withholds_the_forest()
    {
        Assert.Null(Map(Plan("<StmtSimple StatementType=\"SELECT\"><QueryPlan>" +
            Lookup(0, "RID Lookup", "<Object Database=\"[records_db]\" Schema=\"[dbo]\" Table=\"[records]\" IndexKind=\"Heap\" />") +
            "</QueryPlan></StmtSimple>")));
    }

    /// <summary>#451: a seek on the table's primary-key index is the key search, not an index identity.</summary>
    [Fact]
    public void Seek_on_the_primary_key_index_is_a_primary_key_search_and_a_scan_of_it_stays_an_index_scan()
    {
        const string seek = "<StmtSimple StatementType=\"SELECT\"><QueryPlan><RelOp NodeId=\"0\" PhysicalOp=\"Index Seek\"><IndexScan Ordered=\"1\" ScanDirection=\"BACKWARD\"><Object Database=\"[records_db]\" Schema=\"[dbo]\" Table=\"[records]\" Index=\"[PK_records]\" IndexKind=\"NonClustered\" /></IndexScan></RelOp></QueryPlan></StmtSimple>";
        var keyed = Assert.Single(Assert.IsType<ProviderPlanForest>(Map(Plan(seek), primaryKeyIndex: "PK_records")).Nodes);
        Assert.Equal(ProviderPlanOperator.PrimaryKeySearch, keyed.Operation);
        Assert.NotNull(keyed.TargetId);
        Assert.Null(keyed.IndexId);
        Assert.Null(keyed.LogicalIndexName);

        var unnamed = Assert.Single(Assert.IsType<ProviderPlanForest>(Map(Plan(seek))).Nodes);
        Assert.Equal(ProviderPlanOperator.IndexSearch, unnamed.Operation);
        Assert.NotNull(unnamed.IndexId);

        var scan = Assert.Single(Assert.IsType<ProviderPlanForest>(Map(Plan(seek.Replace("Index Seek", "Index Scan")), primaryKeyIndex: "PK_records")).Nodes);
        Assert.Equal(ProviderPlanOperator.IndexScan, scan.Operation);
    }

    [Fact]
    public void Primary_key_search_under_its_bookmark_lookup_stays_the_single_access_node()
    {
        var forest = Map(Plan(
            "<StmtSimple StatementType=\"SELECT\"><QueryPlan>" +
            "<RelOp NodeId=\"0\" PhysicalOp=\"Nested Loops\"><NestedLoops>" +
            "<RelOp NodeId=\"1\" PhysicalOp=\"Index Seek\"><IndexScan><Object Database=\"[records_db]\" Schema=\"[dbo]\" Table=\"[records]\" Index=\"[PK_records]\" /></IndexScan></RelOp>" +
            Lookup(2, "RID Lookup", "<Object Database=\"[records_db]\" Schema=\"[dbo]\" Table=\"[records]\" IndexKind=\"Heap\" />") +
            "</NestedLoops></RelOp></QueryPlan></StmtSimple>"), primaryKeyIndex: "PK_records");

        var nodes = Assert.IsType<ProviderPlanForest>(forest).Nodes;
        Assert.Equal([ProviderPlanOperator.Materialize, ProviderPlanOperator.PrimaryKeySearch, ProviderPlanOperator.Materialize], nodes.Select(node => node.Operation));
        Assert.Single(nodes, node => node.TargetId is not null);
    }

    private static string Lookup(int nodeId, string physicalOp, string obj) =>
        $"<RelOp NodeId=\"{nodeId}\" PhysicalOp=\"{physicalOp}\"><IndexScan Lookup=\"1\" Ordered=\"1\">{obj}</IndexScan></RelOp>";

    private static string Datalength(string expression, string column) =>
        $"<DefinedValue><ColumnReference Column=\"{expression}\" /><ScalarOperator><Intrinsic FunctionName=\"datalength\"><ScalarOperator><Identifier><ColumnReference Schema=\"[dbo]\" Table=\"[records]\" Column=\"{column}\" /></Identifier></ScalarOperator></Intrinsic></ScalarOperator></DefinedValue>";

    private static string Scan(int nodeId) =>
        $"<RelOp NodeId=\"{nodeId}\" PhysicalOp=\"Clustered Index Scan\"><IndexScan><Object Database=\"[records_db]\" Schema=\"[dbo]\" Table=\"[records]\" Index=\"[PK_records]\" /></IndexScan></RelOp>";

    private static string Plan(string statement) =>
        $"<ShowPlanXML xmlns=\"{Namespace}\"><BatchSequence><Batch><Statements>" + statement + "</Statements></Batch></BatchSequence></ShowPlanXML>";

    private static ProviderPlanForest? Map(string rawPlan, IReadOnlyDictionary<string, string>? logicalColumnsByPhysical = null, string? primaryKeyIndex = null) =>
        SqlServerNativePlanMapper.Map(
            rawPlan,
            "records_db",
            "dbo",
            "records",
            new ProviderOpaqueIdentity(Guid.NewGuid()),
            _ => new ProviderOpaqueIdentity(Guid.NewGuid()),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal) { "PK_records" },
            logicalColumnsByPhysical ?? new Dictionary<string, string>(StringComparer.Ordinal),
            primaryKeyIndex);
}
