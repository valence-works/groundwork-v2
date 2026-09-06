using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.PostgreSql;
using Groundwork.Query.Model;
using Groundwork.Store;
using Xunit;

namespace Groundwork.PostgreSql.Tests;

/// <summary>
/// Live acceptance coverage for the provider-neutral bounded-query evidence contract. The rows are
/// written through public sessions so the assertions exercise the same scope binding and rendering
/// path as a consumer, rather than testing a renderer projection in isolation.
/// </summary>
public sealed class PostgreSqlBoundedExecutionEvidenceLiveTests
{
    [SkippableFact]
    public void Public_bounded_query_reports_scope_predicate_nullable_order_lookahead_and_projection()
    {
        using var fixture = new Fixture();

        var result = fixture.Session.Query(Query(fixture.Unit), fixture.Unit.CreateQueryRenderOptions());

        Assert.Equal(2, result.Rows.Count);
        Assert.All(result.Rows, row => Assert.Equal(2, row.Count));
        Assert.Equal("a-null", result.Rows[0]["id"]);
        Assert.Equal(Fixture.ScopeAPayloadNull, result.Rows[0]["payload"]);
        Assert.Equal("a-ready", result.Rows[1]["id"]);
        Assert.Equal(Fixture.ScopeAPayloadReady, result.Rows[1]["payload"]);
        Assert.NotNull(result.NextContinuationToken);

        var evidence = Assert.Single(fixture.Observer.Executions);
        Assert.Equal(ProviderExecutionOperation.BoundedQuery, evidence.Operation);
        Assert.Equal(ProviderExecutionOutcome.Succeeded, evidence.Outcome);
        Assert.Equal(ProviderEvidenceAvailability.Collected, evidence.ShapeAvailability);
        Assert.Equal(ProviderScopeBindingMode.Predicate, evidence.Target.ScopeBinding);
        Assert.Equal(fixture.Unit.Id, evidence.Target.LogicalUnitId);
        Assert.Equal("PostgreSQL", evidence.Provider.Name);
        Assert.Equal(ProviderEvidenceAvailability.NotRequested, evidence.Plan.Availability);
        Assert.Single(fixture.Observer.Commands);

        var shape = Assert.IsType<ProviderBoundedQueryEvidence>(evidence.BoundedQuery);
        Assert.Equal(2, shape.Predicate.Facts.Length);
        var caller = Assert.Single(shape.Predicate.Facts,
            fact => fact.BindingRole == ProviderPredicateBindingRole.Caller);
        Assert.Equal("category", caller.LogicalColumn);
        Assert.Equal(ProviderPredicateOperator.Equal, caller.Operator);
        Assert.Equal(QueryType.String, caller.ValueType);
        Assert.Equal(ProviderPredicateComparison.Ordinal, caller.Comparison);
        Assert.Equal(ProviderPredicateBoundInclusivity.NotApplicable, caller.BoundInclusivity);
        var scope = Assert.Single(shape.Predicate.Facts,
            fact => fact.BindingRole == ProviderPredicateBindingRole.Scope);
        Assert.Equal(ProviderPredicateOperator.Equal, scope.Operator);
        Assert.Equal(QueryType.String, scope.ValueType);
        Assert.Equal(ProviderPredicateComparison.Ordinal, scope.Comparison);
        Assert.NotEqual(caller.BindingId, scope.BindingId);

        Assert.Collection(shape.Ordering,
            status =>
            {
                Assert.Equal("status", status.LogicalColumn);
                Assert.Equal(OrderDirection.Ascending, status.Direction);
                Assert.Equal(NullOrder.First, status.NullPlacement);
                Assert.Contains(ProviderOrderingTransform.NullRank, status.Transforms);
                Assert.Contains(ProviderOrderingTransform.OrdinalStringKey, status.Transforms);
                Assert.Equal(ProviderPredicateComparison.Ordinal, status.Comparison);
            },
            id =>
            {
                Assert.Equal("id", id.LogicalColumn);
                Assert.Equal(OrderDirection.Ascending, id.Direction);
                // The request uses provider-default options without a selected index proof. The
                // shared renderer therefore emits its conservative null-rank guard even though
                // the storage declaration and ColumnRef mark id as non-nullable; evidence records
                // the emitted order expression, not the declaration's nullability.
                Assert.Equal(NullOrder.First, id.NullPlacement);
                Assert.Equal(
                    new[] { ProviderOrderingTransform.NullRank, ProviderOrderingTransform.OrdinalStringKey },
                    id.Transforms.ToArray());
                Assert.Equal(ProviderPredicateComparison.Ordinal, id.Comparison);
            });

        Assert.False(shape.Projection.AllColumns);
        // Native paging retains the ordering key; public materialization hides that extra field.
        Assert.Equal(new[] { "id", "payload", "status" }, shape.Projection.LogicalColumns.ToArray());
        Assert.Equal(ProviderNativeBoundKind.Absent, shape.NativeOffset.Kind);
        Assert.Equal(ProviderNativeBoundKind.Explicit, shape.NativeLimit.Kind);
        Assert.Equal(3, shape.NativeLimit.Value);
        Assert.True(shape.HasLookahead);
        Assert.False(shape.HasContinuation);
        Assert.False(shape.IncludesTotalCount);

        var serialized = JsonSerializer.Serialize(evidence);
        Assert.DoesNotContain(Fixture.ScopeA, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.ScopeB, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.Category, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.ScopeAPayloadNull, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.ScopeAPayloadReady, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(Fixture.ScopeBPayload, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.ConnectionString, serialized, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Public_bounded_query_maps_the_actual_postgresql_plan_without_assuming_access_choice()
    {
        using var fixture = new Fixture();
        fixture.Observer.EvidenceOptions = ProviderExecutionEvidenceOptions.ShapeAndPlans;

        var result = fixture.Session.Query(PlanQuery(fixture.Unit));

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("a-null", result.Rows[0]["id"]);
        Assert.Equal("a-ready", result.Rows[1]["id"]);

        var evidence = Assert.Single(fixture.Observer.Executions);
        Assert.Equal(ProviderExecutionOutcome.Succeeded, evidence.Outcome);
        Assert.Equal(ProviderEvidenceAvailability.Collected, evidence.ShapeAvailability);
        Assert.Equal(ProviderEvidenceAvailability.Collected, evidence.Plan.Availability);
        Assert.Equal(ProviderPlanProvenance.EstimatedExplain, evidence.Plan.Provenance);
        Assert.Equal(2, evidence.Plan.CollectionCommandCount);
        Assert.Single(fixture.Observer.Commands);

        var forest = Assert.IsType<ProviderPlanForest>(evidence.Plan.WinningPlan);
        var access = Assert.Single(forest.Nodes, node => node.TargetId is not null);
        Assert.Equal(evidence.Target.PhysicalTargetId, access.TargetId);
        Assert.Contains(access.Operation, new[]
        {
            ProviderPlanOperator.IndexSearch,
            ProviderPlanOperator.IndexScan,
            ProviderPlanOperator.TableScan,
            ProviderPlanOperator.PrimaryKeySearch
        });
        // The declared index is category/status/id while this plan orders by payload. Every
        // supported native access path therefore requires a real native sort; the access choice
        // itself remains provider/optimizer-owned and is intentionally not pinned here.
        Assert.Contains(forest.Nodes, node => node.Operation == ProviderPlanOperator.Sort);
        Assert.Contains(forest.Nodes, node => node.Operation == ProviderPlanOperator.Limit);
    }

    private static QueryRequest Query(StorageUnit unit)
    {
        var table = new TableId(unit.Name);
        var id = new ColumnRef(table, "id", QueryType.String, isNullable: false, maxLength: 128);
        var category = new ColumnRef(table, "category", QueryType.String, isNullable: false, maxLength: 128);
        var status = new ColumnRef(table, "status", QueryType.String, isNullable: true, maxLength: 128);
        var payload = new ColumnRef(table, "payload", QueryType.String, isNullable: false, maxLength: 256);
        return new QueryRequest(
            table,
            new Predicate.Equal(category, QueryConstant.Of(category, Fixture.Category)),
            [
                new OrderTerm(status, OrderDirection.Ascending, NullOrder.First),
                new OrderTerm(id, OrderDirection.Ascending, NullOrder.First)
            ],
            Projection.ColumnsOnly(id, payload),
            Paging.Keyset(2));
    }

    private static QueryRequest PlanQuery(StorageUnit unit)
    {
        var table = new TableId(unit.Name);
        var id = new ColumnRef(table, "id", QueryType.String, isNullable: false, maxLength: 128);
        var category = new ColumnRef(table, "category", QueryType.String, isNullable: false, maxLength: 128);
        var payload = new ColumnRef(table, "payload", QueryType.String, isNullable: false, maxLength: 256);
        return new QueryRequest(
            table,
            new Predicate.Equal(category, QueryConstant.Of(category, Fixture.Category)),
            [new OrderTerm(payload, OrderDirection.Ascending, NullOrder.First)],
            Projection.ColumnsOnly(id, payload),
            Paging.Keyset(2));
    }

    private sealed class Fixture : IDisposable
    {
        internal const string ScopeA = "scope-a-secret";
        internal const string ScopeB = "scope-b-sentinel";
        internal const string Category = "category-secret";
        internal const string ScopeAPayloadNull = "payload-a-null-secret";
        internal const string ScopeAPayloadReady = "payload-a-ready-secret";
        internal const string ScopeBPayload = "payload-b-sentinel";

        private readonly PostgreSqlFixture database;
        private readonly IStorageProviderConnection connection;

        internal Fixture()
        {
            database = PostgreSqlFixture.OpenOrSkip();
            ConnectionString = database.ConnectionString;
            connection = new PostgreSqlProviderFactory().Create(ConnectionString);
            var name = "pg_bounded_evidence_" + Guid.NewGuid().ToString("N");
            Unit = StorageUnit.Declare(name, name)
                .String("id", 128, column => column.Required())
                .String("category", 128, column => column.Required())
                .String("status", 128, column => column.Nullable())
                .String("payload", 256, column => column.Required())
                .Key("id")
                .Index("by_category_status_id", index => index
                    .Ascending("category")
                    .Ascending("status")
                    .Ascending("id"))
                .Scoped()
                .Build();

            try
            {
                Assert.True(connection.Schema.Apply(Unit).Applied);
                using (var scopeA = connection.OpenOwnedSession(
                           Unit,
                           StorageAccess.Scoped(new StorageScope(ScopeA))))
                {
                    Insert(scopeA, "a-null", null, ScopeAPayloadNull);
                    Insert(scopeA, "a-ready", "ready", ScopeAPayloadReady);
                    Insert(scopeA, "a-zulu", "zulu", "payload-a-zulu");
                    Insert(scopeA, "a-other", "other", "payload-a-other", "category-other");
                }

                using (var scopeB = connection.OpenOwnedSession(
                           Unit,
                           StorageAccess.Scoped(new StorageScope(ScopeB))))
                    Insert(scopeB, "b-wanted", null, ScopeBPayload);

                Observer = new EvidenceObserver();
                Session = connection.OpenOwnedSession(
                    Unit,
                    StorageAccess.Scoped(new StorageScope(ScopeA)),
                    Observer);
            }
            catch
            {
                connection.Dispose();
                database.Dispose();
                throw;
            }
        }

        internal string ConnectionString { get; }
        internal StorageUnit Unit { get; }
        internal IOwnedStorageSession Session { get; }
        internal EvidenceObserver Observer { get; }

        public void Dispose()
        {
            Session.Dispose();
            connection.Dispose();
            database.Dispose();
        }

        private static void Insert(
            IOwnedStorageSession session,
            string id,
            string? status,
            string payload,
            string category = Category)
        {
            Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(
                new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["category"] = category,
                    ["status"] = status,
                    ["payload"] = payload
                })).Status);
        }
    }

    private sealed class EvidenceObserver : IProviderExecutionObserver
    {
        internal ProviderExecutionEvidenceOptions EvidenceOptions { get; set; } =
            ProviderExecutionEvidenceOptions.ShapeOnly;
        internal List<ProviderCommandEvent> Commands { get; } = [];
        internal List<ProviderExecutionEvidence> Executions { get; } = [];

        ProviderExecutionEvidenceOptions IProviderExecutionObserver.EvidenceOptions => EvidenceOptions;

        public void Observe(ProviderCommandEvent command) => Commands.Add(command);

        public void ObserveExecution(ProviderExecutionEvidence evidence) => Executions.Add(evidence);
    }
}
