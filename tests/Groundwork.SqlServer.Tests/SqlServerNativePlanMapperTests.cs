using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Store;
using Xunit;

namespace Groundwork.SqlServer.Tests;

public sealed class SqlServerNativePlanMapperTests
{
    [Fact]
    public void Preserves_scalar_computation_without_exporting_native_expressions()
    {
        var forest = Map(Plan("""
            <StmtSimple StatementType="SELECT"><QueryPlan>
              <RelOp NodeId="0" PhysicalOp="Compute Scalar"><ComputeScalar>
                <DefinedValues><DefinedValue>
                  <ScalarOperator ScalarString="private-expression-value" />
                </DefinedValue></DefinedValues>
                <RelOp NodeId="1" PhysicalOp="Table Scan"><TableScan>
                  <Object Database="[records_db]" Schema="[dbo]" Table="[records]" />
                </TableScan></RelOp>
              </ComputeScalar></RelOp>
            </QueryPlan></StmtSimple>
            """), new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.NotNull(forest);
        Assert.Equal(ProviderPlanOperator.Compute, forest.Nodes[0].Operation);
        Assert.Null(forest.Nodes[0].TargetId);
        Assert.Equal(0, forest.Nodes[1].ParentId);
        Assert.Equal(ProviderPlanOperator.TableScan, forest.Nodes[1].Operation);
        Assert.DoesNotContain("private-expression-value", System.Text.Json.JsonSerializer.Serialize(forest));
    }

    private const string Namespace = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
    private static readonly ProviderOpaqueIdentity TargetId =
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    [Fact]
    public void Maps_namespaced_top_sort_index_seek_and_preserves_parentage()
    {
        var forest = Map(
            Plan("""
                <StmtSimple StatementType="SELECT">
                  <QueryPlan><RelOp NodeId="0" PhysicalOp="Top"><Top>
                    <RelOp NodeId="1" PhysicalOp="Sort"><Sort><OutputList />
                      <RelOp NodeId="2" PhysicalOp="Index Seek"><IndexScan>
                        <Object Database="[records_db]" Schema="[dbo]" Table="[records]" Index="[ix_records_value]" />
                      </IndexScan></RelOp>
                    </Sort></RelOp>
                  </Top></RelOp></QueryPlan>
                </StmtSimple>
                """),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ix_records_value"] = "value_index"
            },
            ["ix_records_value"]);

        Assert.NotNull(forest);
        Assert.Collection(
            forest!.Nodes,
            node =>
            {
                Assert.Equal(0, node.Id);
                Assert.Null(node.ParentId);
                Assert.Equal(ProviderPlanOperator.Limit, node.Operation);
            },
            node =>
            {
                Assert.Equal(1, node.Id);
                Assert.Equal(0, node.ParentId);
                Assert.Equal(ProviderPlanOperator.Sort, node.Operation);
                Assert.Null(node.SortPurpose);
            },
            node =>
            {
                Assert.Equal(2, node.Id);
                Assert.Equal(1, node.ParentId);
                Assert.Equal(ProviderPlanOperator.IndexSearch, node.Operation);
                Assert.Equal(TargetId, node.TargetId);
                Assert.NotNull(node.IndexId);
                Assert.Equal("value_index", node.LogicalIndexName);
                Assert.Null(node.IsCovering);
            });
    }

    [Fact]
    public void Maps_namespaced_top_n_sort_payload_and_preserves_parentage()
    {
        var forest = Map(
            Plan("""
                <StmtSimple StatementType="SELECT">
                  <QueryPlan><RelOp NodeId="0" PhysicalOp="Sort" LogicalOp="TopN Sort"><TopSort Distinct="0" Rows="2">
                    <OrderBy><OrderByColumn Ascending="1" /></OrderBy>
                    <RelOp NodeId="1" PhysicalOp="Table Scan"><TableScan>
                      <Object Database="[records_db]" Schema="[dbo]" Table="[records]" />
                    </TableScan></RelOp>
                  </TopSort></RelOp></QueryPlan>
                </StmtSimple>
                """),
            new Dictionary<string, string>(StringComparer.Ordinal),
            []);

        Assert.NotNull(forest);
        Assert.Collection(
            forest!.Nodes,
            node =>
            {
                Assert.Equal(0, node.Id);
                Assert.Null(node.ParentId);
                Assert.Equal(ProviderPlanOperator.Sort, node.Operation);
                Assert.Null(node.SortPurpose);
            },
            node =>
            {
                Assert.Equal(1, node.Id);
                Assert.Equal(0, node.ParentId);
                Assert.Equal(ProviderPlanOperator.TableScan, node.Operation);
            });
    }

    [Fact]
    public void Keeps_index_scan_distinct_and_maps_table_scan_without_inventing_index_facts()
    {
        var indexScan = Map(
            AccessPlan("Index Scan", "[ix_records_value]"),
            new Dictionary<string, string>(StringComparer.Ordinal),
            ["ix_records_value"]);
        var tableScan = Map(
            AccessPlan("Table Scan", index: null),
            new Dictionary<string, string>(StringComparer.Ordinal),
            []);

        Assert.Equal(ProviderPlanOperator.IndexScan, Assert.Single(indexScan!.Nodes).Operation);
        var indexNode = Assert.Single(indexScan.Nodes);
        Assert.NotNull(indexNode.IndexId);
        Assert.Null(indexNode.LogicalIndexName);

        var tableNode = Assert.Single(tableScan!.Nodes);
        Assert.Equal(ProviderPlanOperator.TableScan, tableNode.Operation);
        Assert.Null(tableNode.IndexId);
    }

    [Fact]
    public void Rejects_wrong_target_and_index_prefix_lookalikes()
    {
        var wrongTarget = Map(
            AccessPlan("Index Seek", "[ix_records_value]", table: "[records_archive]"),
            new Dictionary<string, string>(StringComparer.Ordinal),
            ["ix_records_value"]);
        var prefixIndex = Map(
            AccessPlan("Index Seek", "[ix_records_value_extra]"),
            new Dictionary<string, string>(StringComparer.Ordinal),
            ["ix_records_value"]);
        var unknownCatalogIndex = Map(
            AccessPlan("Index Seek", "[ix_records_other]"),
            new Dictionary<string, string>(StringComparer.Ordinal),
            ["ix_records_value"]);

        Assert.Null(wrongTarget);
        Assert.Null(prefixIndex);
        Assert.Null(unknownCatalogIndex);
    }

    [Fact]
    public void Fails_closed_for_unknown_operator_namespace_and_malformed_graph()
    {
        var unknown = Map(
            Plan("""
                <StmtSimple StatementType="SELECT"><QueryPlan>
                  <RelOp NodeId="0" PhysicalOp="Filter"><RelOp NodeId="1" PhysicalOp="Table Scan">
                    <TableScan><Object Database="[records_db]" Schema="[dbo]" Table="[records]" /></TableScan>
                  </RelOp></RelOp>
                </QueryPlan></StmtSimple>
                """),
            new Dictionary<string, string>(StringComparer.Ordinal),
            []);
        var duplicateId = Map(
            Plan("""
                <StmtSimple StatementType="SELECT"><QueryPlan>
                  <RelOp NodeId="0" PhysicalOp="Sort"><Sort><RelOp NodeId="0" PhysicalOp="Table Scan">
                    <TableScan><Object Database="[records_db]" Schema="[dbo]" Table="[records]" /></TableScan>
                  </RelOp></Sort></RelOp>
                </QueryPlan></StmtSimple>
                """),
            new Dictionary<string, string>(StringComparer.Ordinal),
            []);
        var extraStatement = Map(
            Plan("""
                <StmtSimple StatementType="SELECT"><QueryPlan>
                  <RelOp NodeId="0" PhysicalOp="Table Scan"><TableScan>
                    <Object Database="[records_db]" Schema="[dbo]" Table="[records]" />
                  </TableScan></RelOp>
                </QueryPlan></StmtSimple>
                <StmtSimple StatementType="SELECT"><QueryPlan>
                  <RelOp NodeId="1" PhysicalOp="Table Scan"><TableScan>
                    <Object Database="[records_db]" Schema="[dbo]" Table="[records]" />
                  </TableScan></RelOp>
                </QueryPlan></StmtSimple>
                """),
            new Dictionary<string, string>(StringComparer.Ordinal),
            []);
        var extraStatementKinds = PlanWithForeignNamespace("""
                <StmtSimple StatementType="SELECT"><QueryPlan>
                  <RelOp NodeId="0" PhysicalOp="Table Scan"><TableScan>
                    <Object Database="[records_db]" Schema="[dbo]" Table="[records]" />
                  </TableScan></RelOp>
                </QueryPlan></StmtSimple>
                <StmtCond StatementType="SELECT" />
                <StmtCursor StatementType="SELECT" />
                <f:StmtSimple StatementType="SELECT" />
                """);
        var duplicatePayload = Map(
            AccessPlan("Index Seek", "[ix_records_value]")
                .Replace(
                    "</IndexScan>",
                    "</IndexScan><IndexScan><Object Database=\"[records_db]\" Schema=\"[dbo]\" Table=\"[records]\" Index=\"[ix_records_value]\" /></IndexScan>",
                    StringComparison.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            ["ix_records_value"]);
        var conflictingPayload = Map(
            AccessPlan("Index Seek", "[ix_records_value]")
                .Replace("</IndexScan>", "</IndexScan><Sort />", StringComparison.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            ["ix_records_value"]);
        var mixedSortPayload = Map(
            Plan("""
                <StmtSimple StatementType="SELECT"><QueryPlan>
                  <RelOp NodeId="0" PhysicalOp="Sort"><Sort>
                    <RelOp NodeId="1" PhysicalOp="Table Scan"><TableScan>
                      <Object Database="[records_db]" Schema="[dbo]" Table="[records]" />
                    </TableScan></RelOp>
                  </Sort><TopSort Distinct="0" Rows="2" /></RelOp>
                </QueryPlan></StmtSimple>
                """),
            new Dictionary<string, string>(StringComparer.Ordinal),
            []);
        var hiddenPayload = Map(
            AccessPlan("Index Seek", "[ix_records_value]")
                .Replace("</IndexScan>", "</IndexScan><Unknown><IndexScan /></Unknown>", StringComparison.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            ["ix_records_value"]);
        var foreignPayload = Map(
            PlanWithForeignNamespace("""
                <StmtSimple StatementType="SELECT"><QueryPlan>
                  <RelOp NodeId="0" PhysicalOp="Index Seek"><f:IndexScan>
                    <Object Database="[records_db]" Schema="[dbo]" Table="[records]" Index="[ix_records_value]" />
                  </f:IndexScan></RelOp>
                </QueryPlan></StmtSimple>
                """),
            new Dictionary<string, string>(StringComparer.Ordinal),
            ["ix_records_value"]);
        var foreignTopSort = Map(
            PlanWithForeignNamespace("""
                <StmtSimple StatementType="SELECT"><QueryPlan>
                  <RelOp NodeId="0" PhysicalOp="Sort"><f:TopSort Distinct="0" Rows="2">
                    <RelOp NodeId="1" PhysicalOp="Table Scan"><TableScan>
                      <Object Database="[records_db]" Schema="[dbo]" Table="[records]" />
                    </TableScan></RelOp>
                  </f:TopSort></RelOp>
                </QueryPlan></StmtSimple>
                """),
            new Dictionary<string, string>(StringComparer.Ordinal),
            []);
        var hiddenRelOpWrapper = Map(
            Plan("""
                <StmtSimple StatementType="SELECT"><QueryPlan>
                  <RelOp NodeId="0" PhysicalOp="Sort"><Sort><Unknown>
                    <RelOp NodeId="1" PhysicalOp="Table Scan"><TableScan>
                      <Object Database="[records_db]" Schema="[dbo]" Table="[records]" />
                    </TableScan></RelOp>
                  </Unknown></Sort></RelOp>
                </QueryPlan></StmtSimple>
                """),
            new Dictionary<string, string>(StringComparer.Ordinal),
            []);
        var malformedIdentifier = Map(
            AccessPlan("Table Scan", index: null, table: "[records]]"),
            new Dictionary<string, string>(StringComparer.Ordinal),
            []);
        var unknownAccessWrapper = Map(
            AccessPlan("Index Seek", "[ix_records_value]")
                .Replace("IndexScan", "UnknownAccess", StringComparison.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            ["ix_records_value"]);
        var wrongNamespace = Plan("<StmtSimple StatementType=\"SELECT\"><QueryPlan /></StmtSimple>")
            .Replace(Namespace, "urn:not-sql-server-showplan", StringComparison.Ordinal);
        var unsupported = SqlServerNativePlanMapper.Map(
            wrongNamespace,
            "records_db",
            "dbo",
            "records",
            TargetId,
            _ => new ProviderOpaqueIdentity(Guid.NewGuid()),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));

        Assert.Null(unknown);
        Assert.Null(duplicateId);
        Assert.Null(extraStatement);
        Assert.Null(Map(extraStatementKinds, new Dictionary<string, string>(StringComparer.Ordinal), []));
        Assert.Null(duplicatePayload);
        Assert.Null(conflictingPayload);
        Assert.Null(mixedSortPayload);
        Assert.Null(hiddenPayload);
        Assert.Null(foreignPayload);
        Assert.Null(foreignTopSort);
        Assert.Null(hiddenRelOpWrapper);
        Assert.Null(malformedIdentifier);
        Assert.Null(unknownAccessWrapper);
        Assert.Null(unsupported);
    }

    private static ProviderPlanForest? Map(
        string rawPlan,
        IReadOnlyDictionary<string, string> logicalIndexes,
        params string[] catalogIndexes) =>
        SqlServerNativePlanMapper.Map(
            rawPlan,
            "records_db",
            "dbo",
            "records",
            TargetId,
            physical => new ProviderOpaqueIdentity(
                physical == "ix_records_value"
                    ? Guid.Parse("22222222-2222-2222-2222-222222222222")
                    : Guid.Parse("33333333-3333-3333-3333-333333333333")),
            logicalIndexes,
            catalogIndexes.ToHashSet(StringComparer.Ordinal));

    private static string Plan(string statement) =>
        $"<ShowPlanXML xmlns=\"{Namespace}\"><BatchSequence><Batch><Statements>" +
        statement + "</Statements></Batch></BatchSequence></ShowPlanXML>";

    private static string PlanWithForeignNamespace(string statement) =>
        $"<ShowPlanXML xmlns=\"{Namespace}\" xmlns:f=\"urn:foreign\"><BatchSequence><Batch><Statements>" +
        statement + "</Statements></Batch></BatchSequence></ShowPlanXML>";

    private static string AccessPlan(
        string operation,
        string? index,
        string table = "[records]")
    {
        var wrapper = operation == "Table Scan" ? "TableScan" : "IndexScan";
        return Plan($"<StmtSimple StatementType=\"SELECT\"><QueryPlan><RelOp NodeId=\"0\" PhysicalOp=\"{operation}\"><{wrapper}><Object Database=\"[records_db]\" Schema=\"[dbo]\" Table=\"{table}\"" +
            (index is null ? "" : $" Index=\"{index}\"") + $" /></{wrapper}></RelOp></QueryPlan></StmtSimple>");
    }
}

[Collection(SqlServerLiveDatabase.Name)]
public sealed class SqlServerNativePlanLiveEvidenceTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public void Public_bounded_query_collects_a_namespaced_native_plan()
    {
        var connectionString = fixture.Reset();
        using var provider = new SqlServerProviderConnection(connectionString);
        var name = "sqlserver_native_plan_" + Guid.NewGuid().ToString("N");
        var unit = StorageUnit.Declare(name, name)
            .String("id", 128, column => column.Required())
            .String("payload", 128, column => column.Required())
            .Key("id")
            .Build();
        Assert.True(provider.Schema.Apply(unit).Applied);

        var observer = new EvidenceObserver();
        var session = provider.OpenSession(unit, StorageAccess.Global, observer);
        observer.EvidenceOptions = ProviderExecutionEvidenceOptions.ShapeAndPlans;
        session.Query(new QueryRequest(
            new TableId(unit.Name),
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(new ColumnRef(new TableId(unit.Name), "id", QueryType.String, false),
                OrderDirection.Ascending,
                NullOrder.First)],
            Projection.ColumnsOnly(
                new ColumnRef(new TableId(unit.Name), "id", QueryType.String, false),
                new ColumnRef(new TableId(unit.Name), "payload", QueryType.String, false)),
            Paging.Keyset(2)));

        var evidence = Assert.Single(observer.Executions);
        Assert.Equal(ProviderEvidenceAvailability.Collected, evidence.Plan.Availability);
        Assert.Equal(ProviderPlanProvenance.ExplainReplay, evidence.Plan.Provenance);
        Assert.NotNull(evidence.Plan.WinningPlan);
        Assert.Contains(evidence.Plan.WinningPlan!.Nodes, node => node.TargetId is not null);
        Assert.Equal(4, evidence.Plan.CollectionCommandCount);
        Assert.Single(observer.Commands);
    }

    private sealed class EvidenceObserver : IProviderExecutionObserver
    {
        internal ProviderExecutionEvidenceOptions EvidenceOptions { get; set; } =
            ProviderExecutionEvidenceOptions.ShapeAndPlans;
        internal List<ProviderCommandEvent> Commands { get; } = [];
        internal List<ProviderExecutionEvidence> Executions { get; } = [];

        ProviderExecutionEvidenceOptions IProviderExecutionObserver.EvidenceOptions => EvidenceOptions;

        public void Observe(ProviderCommandEvent command) => Commands.Add(command);

        public void ObserveExecution(ProviderExecutionEvidence evidence) => Executions.Add(evidence);
    }
}
