using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.LiveDatabases;
using Groundwork.MongoDb;
using Groundwork.Query.Model;
using MongoDB.Driver;
using Xunit;

namespace Groundwork.MongoDb.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MongoExplainAssertionCollection
{
    public const string Name = "MongoDB explain assertion tests";
}

[Collection(MongoExplainAssertionCollection.Name)]
public sealed class MongoExecutionEvidenceLiveTests
{
    [SkippableFact]
    public void Live_scoped_query_and_global_point_reads_emit_value_free_terminal_evidence()
    {
        using var database = OwnedMongoDatabase.Create();
        using var connection = new MongoDbProviderFactory().Create(database.ConnectionString);
        var scopedUnit = Unit("live_scoped");
        var globalUnit = Unit("live_global") with
        {
            Id = new StorageUnitId("live_global_" + Guid.NewGuid().ToString("N")),
            Name = "live_global_" + Guid.NewGuid().ToString("N"),
            Scope = ScopePolicy.Global
        };
        Assert.True(connection.Schema.Apply(scopedUnit).Applied);
        Assert.True(connection.Schema.Apply(globalUnit).Applied);

        const string scopeA = "scope-secret-405-a";
        const string scopeB = "scope-secret-405-b";
        const string wantedCategory = "category-secret-405-wanted";
        const string globalId = "global-id-secret-405";
        const string globalPayload = "global-payload-secret-405";
        SeedScoped(connection, scopedUnit, scopeA, scopeB, wantedCategory);
        SeedGlobal(connection, globalUnit, globalId, globalPayload);

        var observer = new RecordingExecutionObserver(ProviderExecutionEvidenceOptions.ShapeOnly);
        var query = BoundedQuery(scopedUnit, wantedCategory);
        var scopedSession = connection.OpenSession(
            scopedUnit,
            MongoStorageAccess.Scoped(new StorageScope(scopeA)),
            observer);
        var result = scopedSession.Query(query);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("scope-a-null", result.Rows[0]["id"]);
        Assert.Equal("scope-a-null-payload", result.Rows[0]["payload"]);
        Assert.Equal("scope-a-ready", result.Rows[1]["id"]);
        Assert.Equal("scope-a-ready-payload", result.Rows[1]["payload"]);
        Assert.All(result.Rows, row => Assert.Equal(2, row.Count));

        var globalSession = connection.OpenSession(globalUnit, MongoStorageAccess.Global, observer);
        var found = globalSession.Read(new MongoStorageKey(new Dictionary<string, object?> { ["id"] = globalId }));
        var missing = globalSession.Read(new MongoStorageKey(
            new Dictionary<string, object?> { ["id"] = "global-missing-id-secret-405" }));

        Assert.NotNull(found);
        Assert.Equal(globalPayload, found!.Values.Values["payload"]);
        Assert.Null(missing);

        var queryEvidence = Assert.Single(observer.Evidence.Where(item => item.Operation == ProviderExecutionOperation.BoundedQuery));
        Assert.Equal(ProviderExecutionOutcome.Succeeded, queryEvidence.Outcome);
        Assert.Equal(ProviderEvidenceAvailability.Collected, queryEvidence.ShapeAvailability);
        Assert.NotNull(queryEvidence.BoundedQuery);
        Assert.Equal(ProviderEvidenceAvailability.NotRequested, queryEvidence.Plan.Availability);
        Assert.Equal(scopedUnit.Id, queryEvidence.Target.LogicalUnitId);
        Assert.Equal(ProviderScopeBindingMode.PhysicalTarget, queryEvidence.Target.ScopeBinding);
        Assert.Equal(
            new[] { "category" },
            queryEvidence.BoundedQuery!.Predicate.Facts.Select(fact => fact.LogicalColumn));
        var predicate = Assert.Single(queryEvidence.BoundedQuery.Predicate.Facts);
        Assert.Equal(ProviderPredicateOperator.Equal, predicate.Operator);
        Assert.Equal(QueryType.String, predicate.ValueType);
        Assert.Equal(ProviderPredicateComparison.Ordinal, predicate.Comparison);
        Assert.Equal(ProviderPredicateBindingRole.Caller, predicate.BindingRole);
        Assert.Equal(2, queryEvidence.BoundedQuery.Ordering.Length);
        var statusOrder = queryEvidence.BoundedQuery.Ordering[0];
        Assert.Equal("status", statusOrder.LogicalColumn);
        Assert.Equal(OrderDirection.Ascending, statusOrder.Direction);
        Assert.Equal(NullOrder.First, statusOrder.NullPlacement);
        Assert.Contains(ProviderOrderingTransform.NullRank, statusOrder.Transforms);
        Assert.Contains(ProviderOrderingTransform.OrdinalStringKey, statusOrder.Transforms);
        Assert.Equal("id", queryEvidence.BoundedQuery.Ordering[1].LogicalColumn);
        Assert.Equal(ProviderNativeBoundKind.Explicit, queryEvidence.BoundedQuery.NativeOffset.Kind);
        Assert.Equal(0, queryEvidence.BoundedQuery.NativeOffset.Value);
        Assert.Equal(ProviderNativeBoundKind.Explicit, queryEvidence.BoundedQuery.NativeLimit.Kind);
        Assert.Equal(3, queryEvidence.BoundedQuery.NativeLimit.Value);
        Assert.True(queryEvidence.BoundedQuery.HasLookahead);
        Assert.False(queryEvidence.BoundedQuery.HasContinuation);
        Assert.False(queryEvidence.BoundedQuery.IncludesTotalCount);
        Assert.False(queryEvidence.BoundedQuery.Projection.AllColumns);
        Assert.Equal(new[] { "id", "payload" }, queryEvidence.BoundedQuery.Projection.LogicalColumns);

        var pointEvidence = observer.Evidence
            .Where(item => item.Operation == ProviderExecutionOperation.PointRead)
            .ToArray();
        Assert.Equal(2, pointEvidence.Length);
        Assert.All(pointEvidence, item =>
        {
            Assert.Equal(ProviderExecutionOutcome.Succeeded, item.Outcome);
            Assert.Equal(ProviderEvidenceAvailability.Collected, item.ShapeAvailability);
            Assert.Equal(ProviderEvidenceAvailability.NotRequested, item.Plan.Availability);
            Assert.Equal(ProviderScopeBindingMode.Unscoped, item.Target.ScopeBinding);
            Assert.NotNull(item.PointRead);
            var point = item.PointRead!;
            Assert.Single(point.KeyBounds);
            Assert.Equal("id", point.KeyBounds[0].LogicalColumn);
            Assert.Equal(ProviderNativeBoundKind.Explicit, point.NativeLimit.Kind);
            Assert.Equal(1, point.NativeLimit.Value);
            Assert.True(point.MaterializerReadsAtMostOne);
        });

        Assert.Equal(3, observer.Commands.Count);
        Assert.All(observer.Commands, command => Assert.Equal(ProviderCommandKind.Read, command.Kind));
        Assert.Equal(
            new[] { "mongodb.query", "mongodb.read", "mongodb.read" },
            observer.Commands.Select(command => command.Operation));

        var serialized = JsonSerializer.Serialize(observer.Evidence);
        Assert.DoesNotContain(scopeA, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(scopeB, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(wantedCategory, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(globalId, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(globalPayload, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(database.ConnectionString, serialized, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Live_requested_native_plans_for_point_reads_report_unsupported_without_fabricating_plan_facts()
    {
        using var database = OwnedMongoDatabase.Create();
        using var connection = new MongoDbProviderFactory().Create(database.ConnectionString);
        var unit = Unit("live_plan") with
        {
            Id = new StorageUnitId("live_plan_" + Guid.NewGuid().ToString("N")),
            Name = "live_plan_" + Guid.NewGuid().ToString("N"),
            Scope = ScopePolicy.Global
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        const string id = "plan-id-secret-405";
        const string payload = "plan-payload-secret-405";
        SeedGlobal(connection, unit, id, payload);

        var observer = new RecordingExecutionObserver(ProviderExecutionEvidenceOptions.ShapeAndPlans);
        var session = connection.OpenSession(unit, MongoStorageAccess.Global, observer);
        var found = session.Read(new MongoStorageKey(new Dictionary<string, object?> { ["id"] = id }));
        var missing = session.Read(new MongoStorageKey(
            new Dictionary<string, object?> { ["id"] = "plan-missing-id-secret-405" }));

        Assert.NotNull(found);
        Assert.Equal(payload, found!.Values.Values["payload"]);
        Assert.Null(missing);
        Assert.Equal(2, observer.Evidence.Count);
        Assert.All(observer.Evidence, evidence =>
        {
            Assert.Equal(ProviderExecutionOperation.PointRead, evidence.Operation);
            Assert.Equal(ProviderExecutionOutcome.Succeeded, evidence.Outcome);
            Assert.Equal(ProviderEvidenceAvailability.Collected, evidence.ShapeAvailability);
            Assert.Equal(ProviderEvidenceAvailability.Unsupported, evidence.Plan.Availability);
            Assert.Null(evidence.Plan.Provenance);
            Assert.Null(evidence.Plan.ChosenPhysicalIndexId);
            Assert.Null(evidence.Plan.WinningPlan);
        });
        Assert.Equal(2, observer.Commands.Count);
        Assert.All(observer.Commands, command => Assert.Equal("mongodb.read", command.Operation));
    }

    [SkippableFact]
    public void Live_bounded_query_with_requested_native_plans_emits_collected_explain_forest()
    {
        using var database = OwnedMongoDatabase.Create();
        using var connection = new MongoDbProviderFactory().Create(database.ConnectionString);
        var unit = Unit("live_bounded_plan");
        Assert.True(connection.Schema.Apply(unit).Applied);

        const string scope = "scope-secret-405-plan";
        const string wantedCategory = "category-secret-405-plan";
        SeedScoped(connection, unit, scope, "scope-secret-405-plan-other", wantedCategory);

        var observer = new RecordingExecutionObserver(ProviderExecutionEvidenceOptions.ShapeAndPlans);
        var session = connection.OpenSession(
            unit,
            MongoStorageAccess.Scoped(new StorageScope(scope)),
            observer);
        var result = session.Query(BoundedQuery(unit, wantedCategory));

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("scope-a-null-payload", result.Rows[0]["payload"]);
        Assert.Equal("scope-a-ready-payload", result.Rows[1]["payload"]);

        var evidence = Assert.Single(observer.Evidence);
        Assert.Equal(ProviderExecutionOperation.BoundedQuery, evidence.Operation);
        Assert.Equal(ProviderExecutionOutcome.Succeeded, evidence.Outcome);
        Assert.Equal(ProviderEvidenceAvailability.Collected, evidence.ShapeAvailability);
        Assert.NotNull(evidence.BoundedQuery);
        Assert.Equal(ProviderEvidenceAvailability.Collected, evidence.Plan.Availability);
        Assert.Equal(ProviderPlanProvenance.ExplainReplay, evidence.Plan.Provenance);
        Assert.Equal(1, evidence.Plan.CollectionCommandCount);
        Assert.NotNull(evidence.Plan.WinningPlan);
        var forest = evidence.Plan.WinningPlan!;
        Assert.NotEmpty(forest.Nodes);
        Assert.Contains(forest.Nodes, node => node.TargetId is not null);

        Assert.Single(observer.Commands);
        Assert.Equal("mongodb.query", observer.Commands[0].Operation);
    }

    [SkippableFact]
    public void Live_structured_transactional_query_preserves_legacy_explain_assertion_refusal()
    {
        using var database = OwnedMongoDatabase.Create();
        using var connection = new MongoDbProviderFactory().Create(database.ConnectionString);
        Skip.If(connection.ProviderSequenceFit is ProviderFit.Unsupported,
            "MongoDB deployment does not support transactions.");
        var unit = Unit("live_transaction") with
        {
            Scope = ScopePolicy.Global,
            Indexes =
            [
                new IndexDefinition
                {
                    Name = "by_category",
                    Columns = [new IndexColumn("category")]
                }
            ]
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        SeedGlobal(connection, unit, "transaction-id-secret-405", "transaction-payload-secret-405");

        var previousFlag = Environment.GetEnvironmentVariable("GW_EXPLAIN_ASSERT");
        Environment.SetEnvironmentVariable("GW_EXPLAIN_ASSERT", "1");
        try
        {
            var observer = new RecordingExecutionObserver(ProviderExecutionEvidenceOptions.ShapeAndPlans);
            var options = new QueryRenderOptions(
                [new QueryIndexDeclaration(
                    "by_category",
                    [new QueryIndexColumn("category", false, QueryType.String)],
                    QueryIndexPinning.ProviderDefault)],
                selectedIndex: "by_category");
            using var work = connection.BeginUnitOfWork(MongoStorageAccess.Global, observer, unit);
            var session = work.OpenSession(unit);

            var refusal = Assert.Throws<InvalidOperationException>(() =>
                session.Query(BoundedQuery(unit, "global-category"), options));

            Assert.Contains("cannot run inside a transaction", refusal.Message, StringComparison.Ordinal);
            var evidence = Assert.Single(observer.Evidence);
            Assert.Equal(ProviderExecutionOutcome.Succeeded, evidence.Outcome);
            Assert.Equal(ProviderEvidenceAvailability.Unsupported, evidence.Plan.Availability);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GW_EXPLAIN_ASSERT", previousFlag);
        }
    }

    private static StorageUnit Unit(string suffix) => new()
    {
        Id = new StorageUnitId("live_" + suffix + "_" + Guid.NewGuid().ToString("N")),
        Name = "live_" + suffix + "_" + Guid.NewGuid().ToString("N"),
        Scope = ScopePolicy.Scoped,
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, MaxLength = 128, IsNullable = false },
            new() { Name = "category", Type = PortableType.String, MaxLength = 128, IsNullable = false },
            new() { Name = "status", Type = PortableType.String, MaxLength = 128, IsNullable = true },
            new() { Name = "payload", Type = PortableType.String, MaxLength = 256, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static QueryRequest BoundedQuery(StorageUnit unit, string category) {
        var table = new TableId(unit.Name);
        var id = new ColumnRef(table, "id", QueryType.String, isNullable: false);
        var categoryColumn = new ColumnRef(table, "category", QueryType.String, isNullable: false);
        var status = new ColumnRef(table, "status", QueryType.String, isNullable: true);
        var payload = new ColumnRef(table, "payload", QueryType.String, isNullable: false);
        return new QueryRequest(
            table,
            new Predicate.Equal(categoryColumn, QueryConstant.Of(categoryColumn, category)),
            [
                new OrderTerm(status, OrderDirection.Ascending, NullOrder.First),
                new OrderTerm(id, OrderDirection.Ascending)
            ],
            Projection.ColumnsOnly(id, payload),
            Paging.OffsetLimit(0, 2));
    }

    private static void SeedScoped(
        IMongoProviderConnection connection,
        StorageUnit unit,
        string scopeA,
        string scopeB,
        string wantedCategory)
    {
        var first = connection.OpenSession(unit, MongoStorageAccess.Scoped(new StorageScope(scopeA)));
        Assert.Equal(MongoWriteOutcomeStatus.Inserted, first.Insert(Values("scope-a-null", wantedCategory, null, "scope-a-null-payload")).Status);
        Assert.Equal(MongoWriteOutcomeStatus.Inserted, first.Insert(Values("scope-a-ready", wantedCategory, "ready", "scope-a-ready-payload")).Status);
        Assert.Equal(MongoWriteOutcomeStatus.Inserted, first.Insert(Values("scope-a-other", "other", "ready", "scope-a-other-payload")).Status);

        var second = connection.OpenSession(unit, MongoStorageAccess.Scoped(new StorageScope(scopeB)));
        Assert.Equal(MongoWriteOutcomeStatus.Inserted, second.Insert(Values("scope-b-match", wantedCategory, null, "scope-b-match-payload")).Status);
    }

    private static void SeedGlobal(
        IMongoProviderConnection connection,
        StorageUnit unit,
        string id,
        string payload)
    {
        var session = connection.OpenSession(
            unit,
            unit.Scope == ScopePolicy.Global
                ? MongoStorageAccess.Global
                : MongoStorageAccess.Scoped(new StorageScope("seed-global-scope")));
        Assert.Equal(MongoWriteOutcomeStatus.Inserted,
            session.Insert(Values(id, "global-category", "ready", payload)).Status);
    }

    private static MongoStorageValues Values(string id, string category, string? status, string payload) =>
        new(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["category"] = category,
            ["status"] = status,
            ["payload"] = payload
        });

    private sealed class RecordingExecutionObserver(ProviderExecutionEvidenceOptions options) : IProviderExecutionObserver
    {
        public ProviderExecutionEvidenceOptions EvidenceOptions { get; } = options;
        public List<ProviderExecutionEvidence> Evidence { get; } = [];
        public List<ProviderCommandEvent> Commands { get; } = [];

        public void Observe(ProviderCommandEvent command) => Commands.Add(command);

        public void ObserveExecution(ProviderExecutionEvidence evidence) => Evidence.Add(evidence);
    }

    private sealed class OwnedMongoDatabase : IDisposable
    {
        private readonly MongoClient client;

        private OwnedMongoDatabase(string connectionString, string databaseName)
        {
            ConnectionString = connectionString;
            this.databaseName = databaseName;
            client = new MongoClient(connectionString);
        }

        private readonly string databaseName;

        public string ConnectionString { get; }

        public static OwnedMongoDatabase Create()
        {
            var configured = LiveMongo.Required();
            var databaseName = "gw405_evidence_" + Guid.NewGuid().ToString("N");
            var url = new MongoUrlBuilder(configured) { DatabaseName = databaseName };
            return new OwnedMongoDatabase(url.ToMongoUrl().ToString(), databaseName);
        }

        public void Dispose()
        {
            client.DropDatabase(databaseName);
        }
    }
}
