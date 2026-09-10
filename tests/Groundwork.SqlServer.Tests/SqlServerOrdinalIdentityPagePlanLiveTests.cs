using Groundwork.Kernel;
using Groundwork.LiveDatabases;
using Groundwork.Query.Model;
using Groundwork.Store;
using Xunit;

namespace Groundwork.SqlServer.Tests;

/// <summary>
/// A unit ordering through a persisted ordinal identity without a nominated index (#443) collects a native
/// plan on its first page and on every keyset continuation page; the shape mirrors a consumer's log unit
/// whose route predicate column is nullable and which declares two ordinal-identity indexes.
/// </summary>
[Collection(SqlServerLiveDatabase.Name)]
public sealed class SqlServerOrdinalIdentityPagePlanLiveTests(SqlServerFixture database)
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
        Assert.Single(second.Rows);

        var pages = observer.Executions.Where(evidence => evidence.Operation == ProviderExecutionOperation.BoundedQuery).ToArray();
        Assert.Equal(2, pages.Length);
        Assert.All(pages, evidence =>
        {
            Assert.Equal(ProviderExecutionOutcome.Succeeded, evidence.Outcome);
            Assert.Equal(ProviderEvidenceAvailability.Collected, evidence.Plan.Availability);
        });
    }

    private sealed class Observer : IProviderExecutionObserver
    {
        internal List<ProviderCommandEvent> Commands { get; } = [];
        internal List<ProviderExecutionEvidence> Executions { get; } = [];
        ProviderExecutionEvidenceOptions IProviderExecutionObserver.EvidenceOptions => ProviderExecutionEvidenceOptions.ShapeAndPlans;
        public void Observe(ProviderCommandEvent command) => Commands.Add(command);
        public void ObserveExecution(ProviderExecutionEvidence evidence) => Executions.Add(evidence);
    }
}
