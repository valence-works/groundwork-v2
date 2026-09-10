using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Store;
using Xunit;

namespace Groundwork.PostgreSql.Tests;

/// <summary>
/// A unit that orders through a persisted ordinal identity without a nominated index (#443) collects
/// a native plan on its first page and on every keyset continuation page (#422).
/// </summary>
public sealed class PostgreSqlOrdinalIdentityPagePlanLiveTests
{
    [SkippableFact]
    public void Un_nominated_ordinal_identity_pages_collect_their_native_plans()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        using var connection = new PostgreSqlProviderFactory().Create(database.ConnectionString);
        var name = "pg_span_page_" + Guid.NewGuid().ToString("N");
        var unit = StorageUnit.Declare(name, name)
            .Int64("sequence", column => column.Required().ProviderSequence())
            .String("id", 128, column => column.Required())
            .String("payload", 256, column => column.Required())
            .String("traceKey", 64, column => column.Required())
            .String("spanId", 128, column => column.Required().OrdinalIdentity("__groundwork_ordinal_spanId"))
            .Timestamp("startTime", column => column.Required())
            .Key("sequence")
            .Index("trace_detail", index => index
                .UseOrdinalIdentities()
                .Ascending("traceKey")
                .Ascending("startTime")
                .Ascending("spanId")
                .Ascending("sequence"))
            .Scoped()
            .Build();
        Assert.True(connection.Schema.Apply(unit).Applied);

        var observer = new Observer();
        using var session = connection.OpenOwnedSession(unit, StorageAccess.Scoped(new StorageScope("scope-a")), observer);
        for (var index = 0; index < 3; index++)
            Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = $"span-{index}",
                ["payload"] = "{}",
                ["traceKey"] = "trace-1",
                ["spanId"] = $"span-{index}",
                ["startTime"] = new DateTimeOffset(2026, 9, 10, 0, 0, index, TimeSpan.Zero)
            })).Status);

        var table = new TableId(unit.Name);
        var traceKey = new ColumnRef(table, "traceKey", QueryType.String, isNullable: false, maxLength: 64);
        var spanId = new ColumnRef(table, "spanId", QueryType.String, isNullable: false, maxLength: 128);
        var startTime = new ColumnRef(table, "startTime", QueryType.DateTimeOffset, isNullable: false);
        var sequence = new ColumnRef(table, "sequence", QueryType.Int64, isNullable: false);
        var request = new QueryRequest(
            table,
            new Predicate.Equal(traceKey, QueryConstant.Of(traceKey, "trace-1")),
            [
                new OrderTerm(startTime, OrderDirection.Ascending, NullOrder.Last),
                new OrderTerm(spanId, OrderDirection.Ascending, NullOrder.Last),
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
            var forest = Assert.IsType<ProviderPlanForest>(evidence.Plan.WinningPlan);
            var access = Assert.Single(forest.Nodes, node => node.TargetId is not null);
            Assert.Equal(ProviderPlanOperator.IndexSearch, access.Operation);
            Assert.Equal("trace_detail", access.LogicalIndexName);
        });
        // The un-nominated ordinal ordering continues through the native row-value tuple, so every page
        // is one index search under its limit; the lexicographic disjunction could plan as a bitmap-or.
        Assert.All(pages, evidence => Assert.Equal(
            new[] { ProviderPlanOperator.Limit, ProviderPlanOperator.IndexSearch },
            Assert.IsType<ProviderPlanForest>(evidence.Plan.WinningPlan).Nodes.Select(node => node.Operation).ToArray()));
        var continuation = Assert.IsType<ProviderBoundedQueryEvidence>(pages[1].BoundedQuery);
        Assert.True(continuation.HasContinuation);
        Assert.Equal(ProviderContinuationForm.Tuple, Assert.IsType<ProviderContinuationPredicate>(continuation.Continuation).Form);
    }

    private sealed class Observer : IProviderExecutionObserver
    {
        internal List<ProviderExecutionEvidence> Executions { get; } = [];
        ProviderExecutionEvidenceOptions IProviderExecutionObserver.EvidenceOptions => ProviderExecutionEvidenceOptions.ShapeAndPlans;
        public void Observe(ProviderCommandEvent command) { }
        public void ObserveExecution(ProviderExecutionEvidence evidence) => Executions.Add(evidence);
    }
}
