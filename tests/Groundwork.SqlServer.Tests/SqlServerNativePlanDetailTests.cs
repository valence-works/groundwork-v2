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
            "<RelOp NodeId=\"1\" PhysicalOp=\"Compute Scalar\"><ComputeScalar><DefinedValues><DefinedValue><ColumnReference Column=\"Expr1003\" />" +
            "<ScalarOperator><Intrinsic FunctionName=\"datalength\"><ScalarOperator><Identifier><ColumnReference Schema=\"[dbo]\" Table=\"[records]\" Column=\"id\" /></Identifier></ScalarOperator></Intrinsic></ScalarOperator>" +
            "</DefinedValue></DefinedValues>" + Scan(2) + "</ComputeScalar></RelOp></Sort></RelOp></QueryPlan></StmtSimple>"));

        var sort = Assert.Single(Assert.IsType<ProviderPlanForest>(forest).Nodes, node => node.Operation == ProviderPlanOperator.Sort);
        var keys = sort.Details!.NativeSortKeys!.Value;
        Assert.Equal(2, keys.Length);
        Assert.Equal("id", keys[0].LogicalColumn);
        Assert.Equal(ProviderOrderingTransform.OrdinalStringKey, Assert.Single(keys[0].Transforms));
        Assert.Equal("id", keys[1].LogicalColumn);
        Assert.Empty(keys[1].Transforms);
        Assert.Null(sort.Details.Spill);
        Assert.Equal(ProviderNativeBoundKind.Unknown, sort.Details.NativeLimit.Kind);
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

    private static string Scan(int nodeId) =>
        $"<RelOp NodeId=\"{nodeId}\" PhysicalOp=\"Clustered Index Scan\"><IndexScan><Object Database=\"[records_db]\" Schema=\"[dbo]\" Table=\"[records]\" Index=\"[PK_records]\" /></IndexScan></RelOp>";

    private static string Plan(string statement) =>
        $"<ShowPlanXML xmlns=\"{Namespace}\"><BatchSequence><Batch><Statements>" + statement + "</Statements></Batch></BatchSequence></ShowPlanXML>";

    private static ProviderPlanForest? Map(string rawPlan) =>
        SqlServerNativePlanMapper.Map(
            rawPlan,
            "records_db",
            "dbo",
            "records",
            new ProviderOpaqueIdentity(Guid.NewGuid()),
            _ => new ProviderOpaqueIdentity(Guid.NewGuid()),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal) { "PK_records" },
            new Dictionary<string, string>(StringComparer.Ordinal));
}
