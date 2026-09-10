using Groundwork.Kernel;
using Groundwork.LiveDatabases;
using Groundwork.Query.Model;
using Groundwork.Store;
using Xunit;
using Xunit.Abstractions;

namespace Groundwork.SqlServer.Tests;

/// <summary>
/// A unit ordering through a persisted ordinal identity without a nominated index (#443) collects a native
/// plan on its first page and on every keyset continuation page; the shape mirrors a consumer's log unit
/// whose route predicate column is nullable.
/// </summary>
[Collection(SqlServerLiveDatabase.Name)]
public sealed class SqlServerOrdinalIdentityPagePlanLiveTests(SqlServerFixture database, ITestOutputHelper output)
{
    [SkippableFact]
    public void Un_nominated_ordinal_identity_pages_collect_their_native_plans()
    {
        var connectionString = database.Reset();
        using var connection = new SqlServerProviderFactory().Create(connectionString);
        var name = "w2_log_page_" + Guid.NewGuid().ToString("N");
        var unit = StorageUnit.Declare(name, name)
            .Int64("sequence", column => column.Required().ProviderSequence())
            .String("id", 128, column => column.Required().OrdinalIdentity("__groundwork_ordinal_id"))
            .String("payload", 256, column => column.Required())
            .String("traceKey", 64)
            .String("body", column => column.Required())
            .Timestamp("timestamp", column => column.Required())
            .Key("sequence")
            .Index("by_timestamp", index => index
                .UseOrdinalIdentities()
                .Descending("timestamp")
                .Ascending("id")
                .Ascending("sequence"))
            .Index("trace_detail", index => index
                .UseOrdinalIdentities()
                .Ascending("traceKey")
                .Ascending("timestamp")
                .Ascending("id")
                .Ascending("sequence"))
            .Scoped()
            .Build();
        Assert.True(connection.Schema.Apply(unit).Applied);

        var observer = new Observer();
        using var session = connection.OpenOwnedSession(unit, StorageAccess.Scoped(new StorageScope("scope-a")), observer);
        for (var index = 0; index < 3; index++)
            Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = $"log-{index}",
                ["payload"] = "{}",
                ["traceKey"] = "trace-1",
                ["body"] = "body",
                ["timestamp"] = new DateTimeOffset(2026, 9, 10, 0, 0, index, TimeSpan.Zero)
            })).Status);
        // A consumer's fixture trace carries every retained row: the route predicate matches the whole unit.
        var rows = int.TryParse(Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_PAGE_PLAN_ROWS"), out var parsed) ? parsed : 100_000;
        if (rows > 0)
        {
            using var raw = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
            raw.Open();
            using var seed = raw.CreateCommand();
            seed.CommandTimeout = 1800;
            seed.CommandText = $"""
                WITH n AS (SELECT TOP ({rows}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c)
                INSERT INTO [{unit.Name}] ([id], [payload], [traceKey], [body], [timestamp], [__groundwork_ordinal_id], [__groundwork_scope])
                SELECT CONCAT('seed-', i), 'p', 'trace-1', 'body', DATEADD(SECOND, i % 86400, CAST('2026-09-10T00:00:00+00:00' AS datetimeoffset)), CONCAT('seed-', i), '{(string)ScopeValue(session)}' FROM n;
                UPDATE STATISTICS [{unit.Name}];
                """;
            seed.ExecuteNonQuery();
        }

        var table = new TableId(unit.Name);
        var traceKey = new ColumnRef(table, "traceKey", QueryType.String, isNullable: true, maxLength: 64);
        var id = new ColumnRef(table, "id", QueryType.String, isNullable: false, maxLength: 128);
        var timestamp = new ColumnRef(table, "timestamp", QueryType.DateTimeOffset, isNullable: false);
        var sequence = new ColumnRef(table, "sequence", QueryType.Int64, isNullable: false);
        var request = new QueryRequest(
            table,
            new Predicate.Equal(traceKey, QueryConstant.Of(traceKey, "trace-1")),
            [
                new OrderTerm(timestamp, OrderDirection.Ascending, NullOrder.Last),
                new OrderTerm(id, OrderDirection.Ascending, NullOrder.Last),
                new OrderTerm(sequence, OrderDirection.Ascending, NullOrder.Last)
            ],
            Projection.All,
            Paging.Keyset(2));
        var options = unit.CreateQueryRenderOptions();

        var first = session.Query(request, options);
        Assert.Equal(2, first.Rows.Count);
        Assert.NotNull(first.NextContinuationToken);
        var second = session.Query(
            new QueryRequest(table, request.Where, request.Order, request.Projection, Paging.Continuation(first.NextContinuationToken!, 2)),
            options);
        Assert.NotEmpty(second.Rows);

        foreach (var command in observer.Commands)
            output.WriteLine("COMMAND " + command.Operation + ": " + command.CommandText);
        var pages = observer.Executions.Where(evidence => evidence.Operation == ProviderExecutionOperation.BoundedQuery).ToArray();
        foreach (var evidence in pages)
            output.WriteLine($"PLAN availability={evidence.Plan.Availability} commands={evidence.Plan.CollectionCommandCount} nodes={(evidence.Plan.WinningPlan is null ? "-" : string.Join(",", evidence.Plan.WinningPlan.Nodes.Select(node => node.Operation)))}");
        foreach (var command in observer.Commands.Where(command => command.Operation == "sqlserver.query"))
        {
            try
            {
                using var raw = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
                raw.Open();
                using (var on = raw.CreateCommand()) { on.CommandText = "SET SHOWPLAN_XML ON"; on.ExecuteNonQuery(); }
                using var explain = raw.CreateCommand();
                var text = command.CommandText;
                string TypeOf(string parameter) =>
                    System.Text.RegularExpressions.Regex.IsMatch(text, "FETCH NEXT " + parameter + " ROWS") ? "int = 128"
                    : System.Text.RegularExpressions.Regex.IsMatch(text, "\\[timestamp\\] (>|=|<) " + parameter + "\\b") ? "datetimeoffset = '2026-09-10T00:00:05+00:00'"
                    : System.Text.RegularExpressions.Regex.IsMatch(text, "\\[sequence\\] (>|=|<) " + parameter + "\\b") ? "bigint = 3"
                    : "nvarchar(128) = N'x'";
                var declarations = string.Join(" ", System.Text.RegularExpressions.Regex.Matches(text, "@p(\\d+)\\b")
                    .Select(match => match.Value).Distinct()
                    .Select(parameter => $"DECLARE {parameter} {TypeOf(parameter)};"));
                explain.CommandText = declarations + " " + text;
                var xml = (string)explain.ExecuteScalar()!;
                var ops = System.Xml.Linq.XDocument.Parse(xml).Descendants().Where(element => element.Name.LocalName == "RelOp")
                    .Select(element => element.Attribute("NodeId")?.Value + ":" + element.Attribute("PhysicalOp")?.Value + "/" + element.Attribute("LogicalOp")?.Value +
                        "[" + string.Join(",", element.Elements().Where(child => child.Name.LocalName != "OutputList").Select(child => child.Name.LocalName)) + "]" +
                        "(" + string.Join(",", element.Descendants().Where(child => child.Name.LocalName == "Object").Take(2).Select(o => o.Attribute("Index")?.Value ?? "-")) + ")");
                output.WriteLine("SHOWPLAN " + string.Join(" > ", ops));
            }
            catch (Exception exception)
            {
                output.WriteLine("SHOWPLAN failed: " + exception.Message);
            }
        }
        Assert.All(pages, evidence =>
        {
            Assert.Equal(ProviderExecutionOutcome.Succeeded, evidence.Outcome);
            Assert.Equal(ProviderEvidenceAvailability.Collected, evidence.Plan.Availability);
        });
        Skip.If(Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_PAGE_PLAN_PROBE") is null, "PROBE: " + string.Join(" | ", observer.Executions.Where(e => e.Operation == ProviderExecutionOperation.BoundedQuery).Select(e => e.Plan.Availability.ToString())));
    }

    private static object ScopeValue(IOwnedStorageSession session) => "scope-a";

    private sealed class Observer : IProviderExecutionObserver
    {
        internal List<ProviderCommandEvent> Commands { get; } = [];
        internal List<ProviderExecutionEvidence> Executions { get; } = [];
        ProviderExecutionEvidenceOptions IProviderExecutionObserver.EvidenceOptions => ProviderExecutionEvidenceOptions.ShapeAndPlans;
        public void Observe(ProviderCommandEvent command) => Commands.Add(command);
        public void ObserveExecution(ProviderExecutionEvidence evidence) => Executions.Add(evidence);
    }
}
