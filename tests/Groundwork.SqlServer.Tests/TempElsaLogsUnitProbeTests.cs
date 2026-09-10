using Groundwork.Kernel;
using Groundwork.LiveDatabases;
using Groundwork.Query.Model;
using Groundwork.Store;
using Xunit;
using Xunit.Abstractions;

namespace Groundwork.SqlServer.Tests;

[Collection(SqlServerLiveDatabase.Name)]
public sealed class TempElsaLogsUnitProbeTests(SqlServerFixture database, ITestOutputHelper output)
{
    [SkippableFact]
    public void Elsa_logs_unit_page_zero_collects_its_plan()
    {
        var connectionString = database.Reset();
        using var connection = new SqlServerProviderFactory().Create(connectionString);
        var unit = StorageUnit.Declare("elsa-otel-logs-v2", "elsa_otel_logs_v2")
            .Int64("sequence", c => c.Required().ProviderSequence())
            .String("id", 128, c => { c.Required(); c.OrdinalIdentity("__groundwork_ordinal_id"); })
            .Json("payload", c => c.Required())
            .String("resourceId", 512, c => c.Required())
            .String("serviceName", 512)
            .String("traceId", 64)
            .String("traceKey", 64)
            .String("spanId", 256)
            .String("severityText", c => c.Required())
            .Int64("severityNumber")
            .String("body", c => c.Required())
            .Timestamp("timestamp", c => c.Required())
            .Key("sequence")
            .Index("elsa_otel_logs_timestamp", index => index.UseOrdinalIdentities().Descending("timestamp").Ascending("id").Ascending("sequence"))
            .Index("elsa_otel_logs_trace_detail", index => index.UseOrdinalIdentities().Ascending("traceKey").Ascending("timestamp").Ascending("id").Ascending("sequence"))
            .Scoped()
            .AppendIdempotency(TimeSpan.FromHours(1), "elsa_otel_logs_v2_append")
            .Retention(0, "sequence")
            .Build();
        Assert.True(connection.Schema.Apply(unit).Applied);
        var observer = new Observer();
        using var session = connection.OpenOwnedSession(unit, StorageAccess.Scoped(new StorageScope("otel-scope")), observer);
        Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "log-0", ["payload"] = "{}", ["resourceId"] = "r", ["severityText"] = "INFO", ["body"] = "b",
            ["traceKey"] = "trace-1", ["timestamp"] = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero)
        })).Status);
        using (var raw = new Microsoft.Data.SqlClient.SqlConnection(connectionString))
        {
            raw.Open();
            using var seed = raw.CreateCommand();
            seed.CommandTimeout = 1800;
            seed.CommandText = """
                WITH n AS (SELECT TOP (100000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c)
                INSERT INTO [elsa_otel_logs_v2] ([id], [payload], [resourceId], [serviceName], [traceId], [traceKey], [spanId], [severityText], [severityNumber], [body], [timestamp], [__groundwork_ordinal_id], [__groundwork_scope])
                SELECT CONCAT('seed-', i), '{}', 'r', NULL, 'trace-1', 'trace-1', NULL, 'INFO', 9, 'body', DATEADD(SECOND, i % 86400, CAST('2026-09-10T00:00:00+00:00' AS datetimeoffset)), CONCAT('seed-', i), 'otel-scope' FROM n;
                UPDATE STATISTICS [elsa_otel_logs_v2];
                """;
            seed.ExecuteNonQuery();
        }
        var table = new TableId(unit.Name);
        var traceKey = new ColumnRef(table, "traceKey", QueryType.String, isNullable: true, maxLength: 64);
        var id = new ColumnRef(table, "id", QueryType.String, isNullable: false, maxLength: 128);
        var timestamp = new ColumnRef(table, "timestamp", QueryType.DateTimeOffset, isNullable: false);
        var sequence = new ColumnRef(table, "sequence", QueryType.Int64, isNullable: false);
        var request = new QueryRequest(table,
            new Predicate.Equal(traceKey, QueryConstant.Of(traceKey, "trace-1")),
            [new OrderTerm(timestamp, OrderDirection.Ascending, NullOrder.Last), new OrderTerm(id, OrderDirection.Ascending, NullOrder.Last), new OrderTerm(sequence, OrderDirection.Ascending, NullOrder.Last)],
            Projection.All, Paging.Keyset(127));
        var options = unit.CreateQueryRenderOptions();
        var first = session.Query(request, options);
        Assert.Equal(127, first.Rows.Count);
        var evidence = observer.Executions.Single(e => e.Operation == ProviderExecutionOperation.BoundedQuery);
        output.WriteLine($"PLAN availability={evidence.Plan.Availability} commands={evidence.Plan.CollectionCommandCount} nodes={(evidence.Plan.WinningPlan is null ? "-" : string.Join(",", evidence.Plan.WinningPlan.Nodes.Select(n => n.Operation)))}");
        Skip.If(true, "PROBE " + evidence.Plan.Availability);
    }

    private sealed class Observer : IProviderExecutionObserver
    {
        internal List<ProviderExecutionEvidence> Executions { get; } = [];
        ProviderExecutionEvidenceOptions IProviderExecutionObserver.EvidenceOptions => ProviderExecutionEvidenceOptions.ShapeAndPlans;
        public void Observe(ProviderCommandEvent command) { }
        public void ObserveExecution(ProviderExecutionEvidence evidence) => Executions.Add(evidence);
    }
}
