using Groundwork.Extensions.DependencyInjection;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.LiveDatabases;
using Groundwork.MongoDb;
using Groundwork.Query.Model;
using Groundwork.Testing;
using Groundwork.Store;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Text.Json;
using Xunit;

namespace Groundwork.MongoDb.Tests;

public sealed class MongoProviderIntegrationTests
{
    [SkippableFact]
    public void Global_point_read_observer_describes_physical_collection_and_redacted_identity()
    {
        using var connection = OpenConnection();
        var unit = PointReadUnit("global_observer", ScopePolicy.Global);
        var key = "global-point-read-secret";
        var command = ObservePointRead(
            connection,
            unit,
            MongoStorageAccess.Global,
            new Dictionary<string, object?> { ["id"] = key, ["payload"] = "value" },
            new Dictionary<string, object?> { ["id"] = key });

        AssertPointReadCommand(
            command,
            unit.Name,
            [],
            key);
    }

    [SkippableFact]
    public void Scoped_point_read_observer_describes_hashed_collection_and_redacts_scope_and_identity()
    {
        using var connection = OpenConnection();
        var unit = PointReadUnit("scoped_observer", ScopePolicy.Scoped);
        var access = MongoStorageAccess.Scoped(new StorageScope("tenant-point-read-secret"));
        var key = "scoped-point-read-secret";
        var command = ObservePointRead(
            connection,
            unit,
            access,
            new Dictionary<string, object?> { ["id"] = key, ["payload"] = "value" },
            new Dictionary<string, object?> { ["id"] = key });
        var expectedCollection = MongoSchemaCoordinator.CollectionName(
            new MongoAppliedUnit(unit, unit.Name),
            MongoStorageAccess.Scoped(new StorageScope(access.Scope!.Value)));

        AssertPointReadCommand(
            command,
            expectedCollection,
            [],
            key,
            access.Scope.Value);
    }

    [SkippableFact]
    public void Composite_point_read_observer_preserves_key_shape_while_redacting_every_value()
    {
        using var connection = OpenConnection();
        var unit = CompositePointReadUnit();
        var tenantKey = "composite-tenant-secret";
        var recordKey = "composite-record-secret";
        var command = ObservePointRead(
            connection,
            unit,
            MongoStorageAccess.Global,
            new Dictionary<string, object?>
            {
                ["tenantKey"] = tenantKey,
                ["recordKey"] = recordKey,
                ["payload"] = "value"
            },
            new Dictionary<string, object?>
            {
                ["tenantKey"] = tenantKey,
                ["recordKey"] = recordKey
            });

        AssertPointReadCommand(
            command,
            unit.Name,
            ["tenantKey", "recordKey"],
            tenantKey,
            recordKey);
    }

    [SkippableFact]
    public void Joined_explicit_projection_materializes_qualified_source_and_target_rows()
    {
        var connectionString = LiveMongo.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB integration tests.");
        using var connection = new MongoProviderFactory().Create(connectionString!);
        var fixture = JoinFixture("row-guard", ScopePolicy.Global);
        Assert.True(connection.Schema.Apply(fixture.Target).Applied);
        Assert.True(connection.Schema.Apply(fixture.Source).Applied);
        var target = connection.OpenSession(fixture.Target, StorageAccess.Global);
        Assert.True(target.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "customer-a",
            ["score"] = 10
        })).Succeeded);
        var source = connection.OpenSession(fixture.Source, StorageAccess.Global);
        Assert.True(source.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "order-a",
            ["customer_id"] = "customer-a"
        })).Succeeded);
        var sourceId = new ColumnRef(fixture.SourceTable, "id", QueryType.String, isNullable: false);
        var targetId = new ColumnRef(fixture.Join.TargetTable, "id", QueryType.String, isNullable: false);
        var allColumns = new QueryRequest(
            fixture.SourceTable,
            fixture.Join,
            Predicate.AlwaysTrue.Instance,
            [],
            Projection.All,
            Paging.None);
        var observer = new ProviderCommandObserver();
        var session = connection.OpenSession(fixture.Source, StorageAccess.Global, observer);

        var refusal = Assert.Throws<QueryRenderException>(() => session.Query(allColumns));

        Assert.Equal("GW-QUERY-032", refusal.Code);
        Assert.Contains("explicit projection", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(observer.Commands);

        var request = new QueryRequest(
            fixture.SourceTable,
            fixture.Join,
            Predicate.AlwaysTrue.Instance,
            [],
            Projection.ColumnsOnly(sourceId, targetId, fixture.TargetScore),
            Paging.None);
        var result = session.Query(request);

        var row = Assert.Single(result.Rows);
        Assert.Equal("order-a", row[QueryRequestExecution.ResultFieldName(request, sourceId)]);
        Assert.Equal("customer-a", row[QueryRequestExecution.ResultFieldName(request, targetId)]);
        Assert.Equal(10, row[QueryRequestExecution.ResultFieldName(request, fixture.TargetScore)]);
        Assert.Single(observer.Commands);
    }

    [SkippableFact]
    public void Joined_target_reduction_executes_one_native_lookup_pipeline()
    {
        var connectionString = LiveMongo.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB integration tests.");
        using var connection = new MongoProviderFactory().Create(connectionString!);
        var fixture = JoinFixture("target-sum", ScopePolicy.Scoped);
        var access = StorageAccess.Scoped(new StorageScope("tenant-a"));
        Assert.True(connection.Schema.Apply(fixture.Target).Applied);
        Assert.True(connection.Schema.Apply(fixture.Source).Applied);
        var target = connection.OpenSession(fixture.Target, access);
        Assert.True(target.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "customer-a",
            ["score"] = 10
        })).Succeeded);
        Assert.True(target.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "customer-b",
            ["score"] = 20
        })).Succeeded);
        var source = connection.OpenSession(fixture.Source, access);
        foreach (var row in new[]
                 {
                     (Id: "order-a", CustomerId: "customer-a"),
                     (Id: "order-b", CustomerId: "customer-b"),
                     (Id: "order-missing", CustomerId: "missing")
                 })
        {
            Assert.True(source.Insert(new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = row.Id,
                ["customer_id"] = row.CustomerId
            })).Succeeded);
        }
        var observer = new ProviderCommandObserver();
        var querying = connection.OpenSession(fixture.Source, access, observer);
        var request = new QueryRequest(
            fixture.SourceTable,
            fixture.Join,
            Predicate.AlwaysTrue.Instance,
            [],
            Projection.ColumnsOnly(fixture.TargetScore),
            Paging.None,
            new ResultShape.Sum(fixture.TargetScore));

        var result = querying.Query(request);

        Assert.Equal(30L, Assert.Single(result.Rows)["score"]);
        var command = Assert.Single(observer.Commands);
        Assert.Equal("mongodb.query", command.Operation);
        Assert.Contains("Aggregate", command.CommandText, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Privileged_cross_scope_join_refuses_before_command_observation()
    {
        var connectionString = LiveMongo.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB integration tests.");
        using var connection = new MongoProviderFactory().Create(connectionString!);
        var fixture = JoinFixture("cross-scope-guard", ScopePolicy.Scoped);
        Assert.True(connection.Schema.Apply(fixture.Target).Applied);
        Assert.True(connection.Schema.Apply(fixture.Source).Applied);
        var observer = new ProviderCommandObserver();
        var session = connection.OpenSession(
            fixture.Source,
            StorageAccess.PrivilegedAcrossScopes(new StorageAccessAudit(
                "join-guard", "prove joins never fan out across scopes")),
            observer);
        var request = new QueryRequest(
            fixture.SourceTable,
            fixture.Join,
            Predicate.AlwaysTrue.Instance,
            [],
            Projection.All,
            Paging.None);

        var refusal = Assert.Throws<InvalidOperationException>(() => session.QueryAcrossScopes(request));

        Assert.Contains("GW-ACCESS-003", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(observer.Commands);
    }

    [SkippableFact]
    public void Unequal_scope_declared_reference_join_refuses_before_command_observation()
    {
        var connectionString = LiveMongo.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB integration tests.");
        using var connection = new MongoProviderFactory().Create(connectionString!);
        var targetFixture = JoinFixture("unequal-scope-guard", ScopePolicy.Global);
        var sourceFixture = targetFixture with
        {
            Source = targetFixture.Source with { Scope = ScopePolicy.Scoped }
        };
        Assert.True(connection.Schema.Apply(targetFixture.Target).Applied);
        Assert.True(connection.Schema.Apply(sourceFixture.Source).Applied);
        var observer = new ProviderCommandObserver();
        var session = connection.OpenSession(
            sourceFixture.Source,
            StorageAccess.Scoped(new StorageScope("tenant-a")),
            observer);
        using var metadataConnection = Assert.IsType<MongoDbProviderConnection>(
            new MongoDbProviderFactory().Create(connectionString!));
        var metadata = metadataConnection.Database
            .GetCollection<BsonDocument>("__groundwork_metadata");
        metadata.DeleteOne(new BsonDocument(
            "_id",
            "history:" + new PhysicalSchemaTargetIdentity(
                targetFixture.Target.Id,
                MongoSchemaTargets.Provider.Name)));
        var request = new QueryRequest(
            sourceFixture.SourceTable,
            sourceFixture.Join,
            Predicate.AlwaysTrue.Instance,
            [],
            Projection.ColumnsOnly(sourceFixture.TargetScore),
            Paging.None,
            new ResultShape.Sum(sourceFixture.TargetScore));

        var refusal = Assert.Throws<InvalidOperationException>(() => session.Query(request));

        Assert.Contains("GW-ACCESS-003", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(observer.Commands);
    }

    [SkippableFact]
    public void Owned_session_marker_matches_the_opening_path()
    {
        var connectionString = LiveMongo.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB integration tests.");
        using var connection = new MongoProviderFactory().Create(connectionString!);
        var name = "mongo_session_ownership_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
                new ColumnDefinition { Name = "value", Type = PortableType.String, MaxLength = 64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Concurrency = ConcurrencyDeclaration.Optimistic()
        };
        Assert.True(connection.Schema.Apply(unit).Applied);

        Assert.False(connection.OpenSession(unit, StorageAccess.Global) is IOwnedStorageSession);
        using (var work = connection.BeginUnitOfWork(StorageAccess.Global, unit))
            Assert.False(work.OpenSession(unit) is IOwnedStorageSession);

        var owned = connection.OpenOwnedSession(unit, StorageAccess.Global);
        Assert.IsAssignableFrom<IOwnedStorageSession>(owned);
        Assert.False(owned.IsReleased);
        Assert.True(owned.Upsert(
            new StorageValues(new Dictionary<string, object?> { ["id"] = "conflict", ["value"] = "current" }),
            WriteOptions.Unconditional).Succeeded);
        var stale = Assert.IsAssignableFrom<IConcurrencyStorageSession>(owned).ConditionalUpsert(
            new StorageValues(new Dictionary<string, object?> { ["id"] = "conflict", ["value"] = "stale" }),
            WriteOptions.IfVersion(0));
        Assert.Equal(WriteOutcomeStatus.ConcurrencyConflict, stale.Status);
        owned.Dispose();
        Assert.True(owned.IsReleased);
        Assert.Throws<ObjectDisposedException>(() => owned.Read(new StorageKey(
            new Dictionary<string, object?> { ["id"] = "after-release" })));
        Assert.Throws<ObjectDisposedException>(() => { _ = stale.Detail; });
    }

    [SkippableFact]
    public void Deferred_conflict_detail_refuses_stale_schema_before_observing_its_probe()
    {
        var connectionString = LiveMongo.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB integration tests.");
        using var connection = new MongoProviderFactory().Create(connectionString!);
        var name = "mongo_stale_detail_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns =
            [
                new ColumnDefinition
                {
                    Id = "id",
                    Name = "id",
                    Type = PortableType.String,
                    MaxLength = 64,
                    IsNullable = false
                },
                new ColumnDefinition
                {
                    Id = "value",
                    Name = "value",
                    Type = PortableType.String,
                    MaxLength = 64,
                    IsNullable = false
                }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Concurrency = ConcurrencyDeclaration.Optimistic()
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var observer = new ProviderCommandObserver();
        using var retained = connection.OpenOwnedSession(unit, StorageAccess.Global, observer);
        Assert.True(retained.Upsert(
            new StorageValues(new Dictionary<string, object?> { ["id"] = "one", ["value"] = "current" }),
            WriteOptions.Unconditional).Succeeded);
        var conflict = Assert.IsAssignableFrom<IConcurrencyStorageSession>(retained).ConditionalUpsert(
            new StorageValues(new Dictionary<string, object?> { ["id"] = "one", ["value"] = "stale" }),
            WriteOptions.IfVersion(0));
        Assert.Equal(WriteOutcomeStatus.ConcurrencyConflict, conflict.Status);
        var evolved = unit with
        {
            Columns =
            [
                unit.Columns[0],
                unit.Columns[1] with { Name = "body" }
            ]
        };
        Assert.True(connection.Schema.Apply(evolved).Applied);
        var beforeRefusal = observer.RoundTrips;

        var stale = Assert.Throws<StaleStorageSessionException>(() => { _ = conflict.Detail; });

        Assert.Equal(unit.Id, stale.StorageUnitId);
        Assert.Equal(beforeRefusal, observer.RoundTrips);
    }

    [SkippableFact]
    public void Runtime_resolution_uses_kernel_history_not_the_legacy_schema_cache()
    {
        var connectionString = LiveMongo.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB integration tests.");
        using var connection = new MongoDbProviderFactory().Create(connectionString!);
        var unit = RuntimeAdmissionUnit();
        Assert.True(connection.Schema.Apply(unit).Applied);

        var metadata = Assert.IsType<MongoDbProviderConnection>(connection).Database
            .GetCollection<BsonDocument>("__groundwork_metadata");
        var schemaId = "schema:" + unit.Id.Value;
        Assert.Equal(0, metadata.CountDocuments(new BsonDocument("_id", schemaId)));

        using (var historyOnly = new MongoDbProviderFactory().Create(connectionString!))
        {
            var session = historyOnly.OpenSession(unit, MongoStorageAccess.Global);
            Assert.Equal(unit.Name, session.Unit.Name);
        }

        // A plausible legacy cache must not become an alternate declaration authority.
        metadata.ReplaceOne(
            new BsonDocument("_id", schemaId),
            new BsonDocument
            {
                ["_id"] = schemaId,
                ["collection"] = unit.Name,
                ["fingerprint"] = SchemaIdentity.Fingerprint(unit),
                ["key"] = new BsonArray(unit.Key.Columns)
            },
            new ReplaceOptions { IsUpsert = true });
        using (var retainedHistory = new MongoDbProviderFactory().Create(connectionString!))
        {
            var session = retainedHistory.OpenSession(unit, MongoStorageAccess.Global);
            Assert.Equal(unit.Name, session.Unit.Name);
        }

        metadata.DeleteOne(new BsonDocument(
            "_id",
            "history:" + new PhysicalSchemaTargetIdentity(unit.Id, MongoSchemaTargets.Provider.Name)));
        using var noHistory = new MongoDbProviderFactory().Create(connectionString!);
        var refused = Assert.Throws<InvalidOperationException>(() =>
            noHistory.OpenSession(unit, MongoStorageAccess.Global));
        Assert.Contains("has not been applied", refused.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Schema_apply_publishes_kernel_history_and_host_uses_the_same_ready_verdict()
    {
        var connectionString = LiveMongo.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB integration tests.");
        var unit = RuntimeAdmissionUnit();
        using var connection = new MongoProviderFactory().Create(connectionString!);

        Assert.True(connection.Schema.Apply(unit).Applied);
        var runtime = connection.Schema.InspectRuntimeAdmission(unit);
        Assert.True(runtime.IsReady);
        Assert.Equal(GroundworkRuntimeSchemaAdmissionStatus.Ready, runtime.Status);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        Assert.Equal(unit.Name, session.Unit.Name);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGroundwork().AddConnection(options => options
            .UseProvider(new MongoProviderFactory(), connectionString!)
            .AddUnits(unit));
        using var provider = services.BuildServiceProvider(validateScopes: true);
        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        var hostedUnit = Assert.Single(Assert.Single(
            provider.GetRequiredService<GroundworkAdmissionRunner>().Latest!.Connections).Units);
        Assert.Equal(GroundworkAdmissionStatus.Ready, hostedUnit.Status);
    }

    [SkippableFact]
    public async Task Mongo_host_and_store_admission_agree_on_physical_index_drift()
    {
        var connectionString = LiveMongo.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB integration tests.");
        var unit = RuntimeAdmissionUnit();
        using var connection = new MongoProviderFactory().Create(connectionString!);
        Assert.True(connection.Schema.Apply(unit).Applied);

        using (var native = new MongoDbProviderFactory().Create(connectionString!))
            Assert.IsType<MongoDbProviderConnection>(native).Database
                .GetCollection<BsonDocument>(unit.Name).Indexes.DropOne("by_payload");

        var runtime = connection.Schema.InspectRuntimeAdmission(unit);
        Assert.True(runtime.IsReady);
        Assert.True(runtime.Inspection.HasIndexDrift);
        Assert.Equal(GroundworkRuntimeSchemaAdmissionStatus.Degraded, runtime.Status);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        Assert.Equal(unit.Name, session.Unit.Name);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGroundwork().AddConnection(options => options
            .UseProvider(new MongoProviderFactory(), connectionString!)
            .AddUnits(unit));
        using var provider = services.BuildServiceProvider(validateScopes: true);
        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);

        var hostedUnit = Assert.Single(Assert.Single(
            provider.GetRequiredService<GroundworkAdmissionRunner>().Latest!.Connections).Units);
        Assert.Equal(GroundworkAdmissionStatus.Degraded, hostedUnit.Status);
    }

    [SkippableFact]
    public void Missing_declared_key_serving_index_blocks_Mongo_admission()
    {
        using var connection = OpenConnection();
        var unit = RuntimeAdmissionUnit() with
        {
            Id = new StorageUnitId("mongo-key-admission-" + Guid.NewGuid().ToString("N")),
            Name = "MongoKeyAdmission_" + Guid.NewGuid().ToString("N"),
            Indexes = []
        };
        Assert.True(connection.Schema.Apply(unit).Applied);

        Assert.IsType<MongoDbProviderConnection>(connection).Database
            .GetCollection<BsonDocument>(unit.Name).Indexes
            .DropOne(MongoSchemaTargets.DeclaredKeyIndexName);

        var report = connection.InspectSchema(unit, MongoStorageAccess.Global);
        var refusal = Assert.Single(report.ColumnDrift,
            refusal => refusal.Path == "indexes." + MongoSchemaTargets.DeclaredKeyIndexName);
        Assert.Equal("GW-RUNTIME-001", refusal.Code);
        Assert.False(report.IsProcessReady);
        Assert.False(connection.Schema.InspectRuntimeAdmission(unit).IsReady);

        var failure = Assert.Throws<InvalidOperationException>(() =>
            connection.OpenSession(unit, MongoStorageAccess.Global));
        Assert.Contains("declared-key coverage", failure.Message, StringComparison.Ordinal);
        Assert.Contains(MongoSchemaTargets.DeclaredKeyIndexName, failure.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void A_later_scoped_collection_receives_the_declared_indexes()
    {
        using var connection = OpenConnection();
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("issue201-mongo-scoped-" + Guid.NewGuid().ToString("N")),
            Name = "issue201_mongo_scoped_" + Guid.NewGuid().ToString("N"),
            Scope = ScopePolicy.Scoped,
            Columns =
            [
                new() { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new() { Name = "payload", Type = PortableType.String, MaxLength = 64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Indexes = [new IndexDefinition { Name = "by_payload", Columns = [new IndexColumn("payload")] }]
        };

        Assert.True(connection.Schema.Apply(unit).Applied);
        var access = MongoStorageAccess.Scoped(new StorageScope("later-scope"));
        connection.OpenSession(unit, access);
        var collectionName = MongoSchemaCoordinator.CollectionName(
            new MongoAppliedUnit(unit, unit.Name), access);
        var indexNames = Assert.IsType<MongoDbProviderConnection>(connection).Database
            .GetCollection<BsonDocument>(collectionName).Indexes.List().ToList()
            .Select(index => index["name"].AsString)
            .ToArray();

        Assert.Contains("by_payload", indexNames);
        Assert.Contains(MongoSchemaTargets.DeclaredKeyIndexName, indexNames);
    }

    [SkippableFact]
    public void A_63_byte_storage_unit_name_applies_without_provider_rewriting()
    {
        using var connection = OpenConnection();
        // A per-run GUID keeps the name unique across reruns while still landing exactly on the
        // boundary length the test exists to prove. The logical id is likewise per-run, so a
        // rerun never has metadata pointing a stale id at a now-abandoned physical collection.
        var name = ("boundary_" + Guid.NewGuid().ToString("N")).PadRight(
            PortabilityValidator.MaximumPortableIdentifierLength, 'a')[..PortabilityValidator.MaximumPortableIdentifierLength];
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("logical.boundary." + Guid.NewGuid().ToString("N")),
            Name = name,
            Columns = [new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false }],
            Key = new KeyDefinition { Columns = ["id"] }
        };

        Assert.True(connection.Schema.Apply(unit).Applied);
        Assert.True(connection.Schema.Diff(unit).IsEmpty);
    }

    [SkippableFact]
    public void Schema_apply_plans_and_applies_a_column_rename_from_the_applied_ledger()
    {
        using var connection = OpenConnection();
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("mongo-rename-" + Guid.NewGuid().ToString("N")),
            Name = "mongo_rename_" + Guid.NewGuid().ToString("N"),
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new ColumnDefinition { Name = "customer", Id = "customer", Type = PortableType.String, MaxLength = 64 }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };

        Assert.True(connection.Schema.Apply(unit).Applied);
        var renamed = unit with
        {
            Columns =
            [
                unit.Columns[0],
                unit.Columns[1] with { Name = "buyer", Id = "customer" }
            ]
        };

        Assert.Contains(connection.Schema.Diff(renamed).Changes,
            change => change.Kind == SchemaChangeKind.RenameColumn && change.Identity == "buyer");
        Assert.True(connection.Schema.Apply(renamed).Applied);
    }

    [SkippableFact]
    public void Schema_admission_refuses_invalid_aggregation_before_persistence()
    {
        using var connection = OpenConnection();
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("mongo-invalid-aggregation-" + Guid.NewGuid().ToString("N")),
            Name = "mongo_invalid_aggregation_" + Guid.NewGuid().ToString("N"),
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "group", Type = PortableType.String },
                new() { Name = "flag", Type = PortableType.Boolean }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            AggregationProfiles =
            [
                new AggregationProfile
                {
                    Name = "invalid",
                    GroupByColumns = ["group"],
                    Aggregates = [new Aggregate.Sum("total", "flag")]
                }
            ]
        };

        var exception = Assert.Throws<AggregationValidationException>(() => connection.Schema.Apply(unit));

        Assert.Contains(exception.Errors, error => error.Code == "GW-AGG-TYPE-001");
    }

    [Fact]
    public void SetUnion_budget_probe_counts_distinct_values_without_materializing_addToSet()
    {
        var profile = new AggregationProfile
        {
            Name = "summary",
            GroupByColumns = ["group"],
            Aggregates = [new Aggregate.SetUnion("labels", "label", 1)]
        };

        var stages = MongoStorageSession.RenderSetBudgetProbe(profile, (Aggregate.SetUnion)profile.Aggregates[0]);
        var pipeline = string.Join("\n", stages.Select(stage => stage.ToJson()));

        Assert.DoesNotContain("$addToSet", pipeline, StringComparison.Ordinal);
        Assert.Contains("__groundwork_aggregation_set_probe_count", pipeline, StringComparison.Ordinal);
        Assert.Contains("__groundwork_aggregation_set_probe_value", pipeline, StringComparison.Ordinal);
    }

    [Fact]
    public void Native_scoped_pipeline_uses_group_count_order_limit_and_scope_collection_identity()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("mongo-scoped-aggregation-artifact"),
            Name = "mongo_scoped_aggregation_artifact",
            Scope = ScopePolicy.Scoped,
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "group", Type = PortableType.String, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            AggregationProfiles =
            [
                new AggregationProfile
                {
                    Name = "summary",
                    GroupByColumns = ["group"],
                    Aggregates = [new Aggregate.Count("count")],
                    MaxInputRows = 20,
                    MaxGroups = 10
                }
            ]
        };
        var profile = unit.AggregationProfiles.Single();
        var query = new AggregationQuery("summary")
        {
            OrderByTerms =
            [
                new AggregationOrderTerm("count", Groundwork.Kernel.SortDirection.Descending),
                new AggregationOrderTerm("group", Groundwork.Kernel.SortDirection.Ascending)
            ],
            Take = 5
        };

        var stages = MongoStorageSession.RenderNativeAggregationPipeline(unit, profile, query);
        var pipeline = string.Join("\n", stages.Select(stage => stage.ToJson()));
        var applied = new MongoAppliedUnit(unit, unit.Name);
        var firstCollection = MongoSchemaCoordinator.CollectionName(
            applied,
            MongoStorageAccess.Scoped(new StorageScope("tenant-a")));
        var secondCollection = MongoSchemaCoordinator.CollectionName(
            applied,
            MongoStorageAccess.Scoped(new StorageScope("tenant-b")));

        Assert.Contains("\"$group\"", pipeline, StringComparison.Ordinal);
        Assert.Contains("\"count\" : { \"$sum\" : 1", pipeline, StringComparison.Ordinal);
        Assert.Contains("\"$sort\"", pipeline, StringComparison.Ordinal);
        Assert.Contains("\"$limit\" : 5", pipeline, StringComparison.Ordinal);
        Assert.DoesNotContain("$addToSet", pipeline, StringComparison.Ordinal);
        Assert.NotEqual(firstCollection, secondCollection);
        Assert.Contains("__scope__", firstCollection, StringComparison.Ordinal);
        Assert.Contains("__scope__", secondCollection, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Native_set_union_refuses_MaxValues_before_materializing_the_result()
    {
        using var connection = OpenConnection();
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("mongo-aggregation-budget-" + Guid.NewGuid().ToString("N")),
            Name = "mongo_aggregation_budget_" + Guid.NewGuid().ToString("N"),
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "group", Type = PortableType.String, IsNullable = false },
                new() { Name = "label", Type = PortableType.String }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            AggregationProfiles =
            [
                new AggregationProfile
                {
                    Name = "summary",
                    GroupByColumns = ["group"],
                    Aggregates = [new Aggregate.SetUnion("labels", "label", 1)]
                }
            ]
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, MongoStorageAccess.Global);
        session.Insert(new MongoStorageValues(new Dictionary<string, object?>
        {
            ["id"] = "one", ["group"] = "g", ["label"] = "one"
        }));
        session.Insert(new MongoStorageValues(new Dictionary<string, object?>
        {
            ["id"] = "two", ["group"] = "g", ["label"] = "two"
        }));

        var exception = Assert.Throws<AggregationBudgetExceededException>(() =>
            session.Aggregate(new AggregationQuery("summary")));

        Assert.Equal("GW-AGG-BOUND-007", exception.Code);
    }

    [SkippableFact]
    public void Native_EndsWith_does_not_match_a_trailing_newline()
    {
        using var connection = OpenConnection();
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("mongo-aggregation-suffix-" + Guid.NewGuid().ToString("N")),
            Name = "mongo_aggregation_suffix_" + Guid.NewGuid().ToString("N"),
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "group", Type = PortableType.String, IsNullable = false },
                new() { Name = "amount", Type = PortableType.Int64, IsNullable = false },
                new() { Name = "label", Type = PortableType.String }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            AggregationProfiles =
            [
                new AggregationProfile
                {
                    Name = "summary",
                    GroupByColumns = ["group"],
                    Aggregates = [new Aggregate.Sum("total", "amount")]
                }
            ]
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, MongoStorageAccess.Global);
        session.Insert(new MongoStorageValues(new Dictionary<string, object?>
        {
            ["id"] = "newline", ["group"] = "newline", ["amount"] = 1L, ["label"] = "plain\n"
        }));
        session.Insert(new MongoStorageValues(new Dictionary<string, object?>
        {
            ["id"] = "exact", ["group"] = "exact", ["amount"] = 2L, ["label"] = "plain"
        }));

        var label = new ColumnRef(new TableId(unit.Name), "label", QueryType.String);
        var result = session.Aggregate(new AggregationQuery("summary")
        {
            SourcePredicate = new Predicate.Substring(label, "plain", Anchor.EndsWith)
        });

        var row = Assert.Single(result.Rows);
        Assert.Equal("exact", row["group"]);
        Assert.Equal(2L, row["total"]);
    }

    [Fact]
    public void Aggregation_fingerprint_sorts_allowance_entries_like_the_kernel()
    {
        var baseUnit = new StorageUnit
        {
            Id = new StorageUnitId("mongo-aggregation-fingerprint"),
            Name = "mongo_aggregation_fingerprint",
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "group", Type = PortableType.String },
                new() { Name = "amount", Type = PortableType.Int64 }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        var first = baseUnit with
        {
            AggregationProfiles = [FingerprintProfile(["total", "minimum"])]
        };
        var reordered = baseUnit with
        {
            AggregationProfiles = [FingerprintProfile(["minimum", "total"])]
        };

        Assert.Equal(SchemaIdentity.Fingerprint(first), SchemaIdentity.Fingerprint(reordered));

        static AggregationProfile FingerprintProfile(IReadOnlyList<string> aliases) => new()
        {
            Name = "summary",
            GroupByColumns = ["group"],
            Aggregates =
            [
                new Aggregate.Sum("total", "amount"),
                new Aggregate.Min("minimum", "amount")
            ],
            AllowedPredicates = aliases.Select(alias => new AggregationPredicateAllowance
            {
                Alias = alias,
                SupportedPredicates = new HashSet<AggregationPredicateOperator>
                {
                    AggregationPredicateOperator.Equal
                }
            }).ToArray()
        };
    }

    [Fact]
    public void Declaration_fingerprint_and_snapshot_preserve_index_execution_metadata()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("mongo-index-execution-metadata"),
            Name = "mongo_index_execution_metadata",
            Columns =
            [
                new() { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new() { Name = "name", Type = PortableType.String, IsNullable = false, MaxLength = 32 },
                new() { Name = "__groundwork_ordinal_name", Type = PortableType.String, IsNullable = false, MaxLength = 128 }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Indexes =
            [
                new IndexDefinition
                {
                    Name = "by_name",
                    Columns = [new IndexColumn("__groundwork_ordinal_name")]
                }
            ]
        };
        var marked = unit with
        {
            Indexes =
            [
                unit.Indexes[0] with
                {
                    UseOrdinalIdentities = true,
                    IncludedColumns = ["name", "id"]
                }
            ]
        };
        var markerOnly = marked with
        {
            Indexes = [marked.Indexes[0] with { IncludedColumns = null }]
        };

        Assert.NotEqual(SchemaIdentity.Fingerprint(unit), SchemaIdentity.Fingerprint(markerOnly));
        Assert.NotEqual(SchemaIdentity.Fingerprint(markerOnly), SchemaIdentity.Fingerprint(marked));
        Assert.Equal(
            SchemaIdentity.Fingerprint(marked),
            SchemaIdentity.Fingerprint(marked with
            {
                Indexes = [marked.Indexes[0] with { IncludedColumns = ["id", "name"] }]
            }));

        var snapshot = MongoDeclarationSnapshot.Clone(marked);
        var index = Assert.Single(snapshot.Indexes);
        Assert.True(index.UseOrdinalIdentities);
        Assert.Equal(["name", "id"], index.IncludedColumns);
    }

    [Fact]
    public void Aggregation_fingerprint_is_injective_for_delimited_identifiers()
    {
        var baseUnit = new StorageUnit
        {
            Id = new StorageUnitId("mongo-aggregation-canonical-collision"),
            Name = "mongo_aggregation_canonical_collision",
            Columns =
            [
                new() { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new() { Name = "group", Type = PortableType.String },
                new() { Name = "a:b", Type = PortableType.String },
                new() { Name = "c", Type = PortableType.String },
                new() { Name = "a", Type = PortableType.String },
                new() { Name = "b:c", Type = PortableType.String }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        var first = baseUnit with
        {
            AggregationProfiles = [new AggregationProfile
            {
                Name = "summary",
                GroupByColumns = ["group"],
                Aggregates = [new Aggregate.Min("a:b", "c")]
            }]
        };
        var second = baseUnit with
        {
            AggregationProfiles = [new AggregationProfile
            {
                Name = "summary",
                GroupByColumns = ["group"],
                Aggregates = [new Aggregate.Min("a", "b:c")]
            }]
        };

        Assert.NotEqual(SchemaIdentity.Fingerprint(first), SchemaIdentity.Fingerprint(second));
    }

    [SkippableFact]
    public void Profile_only_reducer_alias_and_budget_changes_are_applied()
    {
        using var connection = OpenConnection();
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("mongo-aggregation-profile-drift-" + Guid.NewGuid().ToString("N")),
            Name = "mongo_aggregation_drift_" + Guid.NewGuid().ToString("N"),
            Columns =
            [
                new() { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new() { Name = "group", Type = PortableType.String },
                new() { Name = "amount", Type = PortableType.Int64 }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            AggregationProfiles = [new AggregationProfile
            {
                Name = "summary",
                GroupByColumns = ["group"],
                Aggregates = [new Aggregate.Min("minimum", "amount")],
                MaxGroups = 10,
                MaxInputRows = 100
            }]
        };

        Assert.True(connection.Schema.Apply(unit).Applied);

        var aliasChanged = unit with
        {
            AggregationProfiles = [unit.AggregationProfiles[0] with
            {
                Aggregates = [new Aggregate.Min("total", "amount")]
            }]
        };
        var reducerChanged = aliasChanged with
        {
            AggregationProfiles = [aliasChanged.AggregationProfiles[0] with
            {
                Aggregates = [new Aggregate.Max("total", "amount")]
            }]
        };
        var budgetChanged = reducerChanged with
        {
            AggregationProfiles = [reducerChanged.AggregationProfiles[0] with { MaxGroups = 11 }]
        };

        foreach (var changed in new[] { aliasChanged, reducerChanged, budgetChanged })
        {
            var diff = connection.Schema.Diff(changed);
            var change = Assert.Single(diff.Changes);
            Assert.Equal(SchemaChangeKind.UpdateAggregationProfile, change.Kind);
            Assert.Equal("summary", change.Identity);
            var applied = connection.Schema.Apply(changed);
            Assert.True(applied.Applied);
            Assert.Equal(change, Assert.Single(applied.Diff.Changes));
        }

        var noOp = connection.Schema.Apply(budgetChanged);
        Assert.True(noOp.Applied);
        Assert.True(noOp.IsNoOp);
    }

    [SkippableFact]
    public async Task Provider_passes_the_shipped_conformance_suite_on_both_surfaces()
    {
        var connectionString = LiveMongo.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB integration tests.");
        var url = new MongoUrlBuilder(connectionString) { DatabaseName = "p1conformance_" + Guid.NewGuid().ToString("N") };
        var database = url.ToMongoUrl().ToString();

        // One database, both surfaces: each run proves the whole contract on its own storage units.
        var synchronous = ConformanceSuite.Run(new MongoProviderFactory(), database);
        Assert.True(synchronous.Passed, string.Join(Environment.NewLine,
            synchronous.Failures.Select(failure => $"{failure.Name}: {failure.Failure}")));

        var asynchronous = await ConformanceSuite.RunAsync(new MongoProviderFactory(), database);
        Assert.True(asynchronous.Passed, string.Join(Environment.NewLine,
            asynchronous.Failures.Select(failure => $"{failure.Name}: {failure.Failure}")));
    }

    [SkippableFact]
    public void Live_compare_and_delete_is_transactional_and_exact()
    {
        var connectionString = LiveMongo.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB integration tests.");
        using var native = OpenConnection();
        Assert.True(native.ProviderSequenceFit is ProviderFit.Supported);
        using var connection = new MongoProviderFactory().Create(connectionString!);
        var name = "mongo_compare_delete_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false },
                new ColumnDefinition { Name = "owner", Type = PortableType.String },
                new ColumnDefinition { Name = "fence", Type = PortableType.Int64, IsNullable = false },
                new ColumnDefinition { Name = "amount", Type = PortableType.Decimal, Precision = 12, Scale = 2 }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Concurrency = ConcurrencyDeclaration.Optimistic()
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var marker = new StorageUnit
        {
            Id = new StorageUnitId(name + "_marker"),
            Name = name + "_marker",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false },
                new ColumnDefinition { Name = "value", Type = PortableType.String, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        Assert.True(connection.Schema.Apply(marker).Applied);
        Assert.Contains(connection.Capabilities, capability => capability.Id == BatchWriteCapabilities.CompareAndDelete);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        Assert.Equal(1L, session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "claim-1", ["owner"] = "worker-a", ["fence"] = 7L
        })).Version);
        session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "claim-decimal", ["owner"] = "worker-a", ["fence"] = 7L, ["amount"] = 7m
        }));
        Assert.Equal(WriteOutcomeStatus.Deleted,
            session.CompareAndDelete(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-decimal" }),
                new Dictionary<string, object?> { ["amount"] = 7 }).Status);

        var mismatchObserver = new ProviderCommandObserver();
        var mismatchSession = (ICompareAndDeleteStorageSession)connection.OpenSession(unit, StorageAccess.Global, mismatchObserver);
        Assert.Equal(WriteOutcomeStatus.ComparisonMismatch,
            mismatchSession.CompareAndDelete(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-1" }),
                new Dictionary<string, object?> { ["owner"] = "worker-b", ["fence"] = 7L }).Status);
        Assert.Equal(2, mismatchObserver.RoundTrips);
        Assert.Contains(mismatchObserver.Commands, command => command.Operation == "mongodb.compare-and-delete-read");
        Assert.Equal(2L, session.Update(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "claim-1", ["owner"] = "worker-a", ["fence"] = 7L
        }), WriteOptions.IfVersion(1)).Version);
        var deleteObserver = new ProviderCommandObserver();
        var deleteSession = (ICompareAndDeleteStorageSession)connection.OpenSession(unit, StorageAccess.Global, deleteObserver);
        var deleted = deleteSession.CompareAndDelete(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-1" }),
            new Dictionary<string, object?> { ["owner"] = "worker-a", ["fence"] = 7L });
        Assert.Equal(WriteOutcomeStatus.Deleted, deleted.Status);
        Assert.Equal(2L, deleted.Version);
        Assert.Equal(3, deleteObserver.RoundTrips);
        Assert.Contains(deleteObserver.Commands, command => command.Operation == "mongodb.compare-and-delete");
        Assert.Equal(WriteOutcomeStatus.NotFound,
            session.CompareAndDelete(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-1" }),
                new Dictionary<string, object?> { ["owner"] = "worker-a" }).Status);

        session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "claim-null", ["owner"] = null, ["fence"] = 7L
        }));
        Assert.Equal(WriteOutcomeStatus.Deleted,
            session.CompareAndDelete(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-null" }),
                new Dictionary<string, object?> { ["owner"] = null }).Status);

        session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "claim-omitted", ["fence"] = 7L
        }));
        Assert.Equal(WriteOutcomeStatus.Deleted,
            session.CompareAndDelete(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-omitted" }),
                new Dictionary<string, object?> { ["owner"] = null }).Status);

        session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "claim-2", ["owner"] = "worker-a", ["fence"] = 7L
        }));
        var claimed = session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-2" }))!;
        var reclaimer = connection.OpenSession(unit, StorageAccess.Global);
        Assert.Equal(2L, reclaimer.Update(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "claim-2", ["owner"] = "worker-b", ["fence"] = 8L
        }), WriteOptions.IfVersion(claimed.Version!.Value)).Version);
        using var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit, marker);
        work.Stage(RowWrite.Insert(marker, new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "marker", ["value"] = "must-rollback"
        })));
        var compare = RowWrite.CompareAndDelete(unit,
            new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-2" }),
            new Dictionary<string, object?> { ["owner"] = "worker-a", ["fence"] = 7L });
        work.Stage(compare);
        var exception = Assert.Throws<BatchWriteException>(() => work.CommitWithOutcomes());
        var outcome = Assert.Single(exception.Outcomes);
        Assert.Same(compare, outcome.Write);
        Assert.Equal(WriteOutcomeStatus.ComparisonMismatch, outcome.Outcome.Status);
        var reclaimed = session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-2" }))!;
        Assert.Equal("worker-b", reclaimed.Values.Values["owner"]);
        Assert.Equal(8L, reclaimed.Values.Values["fence"]);
        Assert.Equal(2L, reclaimed.Version);
        Assert.Null(connection.OpenSession(marker, StorageAccess.Global).Read(
            new StorageKey(new Dictionary<string, object?> { ["id"] = "marker" })));
    }

    [SkippableFact]
    public void Customer_schema_is_native_and_catalog_is_read_from_mongodb()
    {
        using var connection = OpenConnection();
        var unit = TestUnits.Customer with
        {
            Id = new StorageUnitId("p1-customer-" + Guid.NewGuid().ToString("N")),
            Name = "P1Customer_" + Guid.NewGuid().ToString("N")
        };

        var first = connection.Schema.Apply(unit);
        var second = connection.Schema.Apply(unit);
        var indexes = connection.Catalog.ReadIndexes(unit.Id);
        using var reopened = new MongoDbProviderFactory().Create(
            LiveMongo.ConnectionString!);
        var reopenedIndexes = reopened.Catalog.ReadIndexes(unit.Id);
        var native = Assert.IsType<MongoDbProviderConnection>(connection).Database
            .GetCollection<BsonDocument>(unit.Name);
        var indexNames = native.Indexes.List().ToList().Select(index => index["name"].AsString).ToArray();

        Assert.False(first.IsNoOp);
        Assert.True(second.IsNoOp);
        Assert.Contains(indexes, index => index.Name == "unique_email" &&
            index.IsUnique && index.MissingValues == MissingValueBehavior.Excluded);
        Assert.Contains(reopenedIndexes, index => index.Name == "unique_email");
        Assert.Contains("unique_email", indexNames);

        var session = connection.OpenSession(unit, MongoStorageAccess.Global);
        var values = CustomerValues("one", "one@example.test");
        Assert.Equal(MongoWriteOutcomeStatus.Inserted, session.Insert(values).Status);
        var loaded = session.Read(new MongoStorageKey(new Dictionary<string, object?> { ["id"] = "one" }));
        Assert.Equal("Ada", loaded!.Values.Values["name"]);
        Assert.Equal(1.2346m, loaded.Values.Values["balance"]);
        Assert.Equal(MongoWriteOutcomeStatus.UniqueViolation,
            session.Insert(CustomerValues("two", "one@example.test")).Status);
        Assert.Equal(MongoWriteOutcomeStatus.Inserted,
            session.Insert(CustomerValues("null-one", null)).Status);
        Assert.Equal(MongoWriteOutcomeStatus.Inserted,
            session.Insert(CustomerValues("null-two", null)).Status);

        var document = native.Find(new BsonDocument("_id", "one")).First();
        Assert.Equal("Ada", document["name"].AsString);
        Assert.Equal(BsonType.Decimal128, document["balance"].BsonType);
        Assert.True(document.Contains("email"));
        Assert.True(native.Find(new BsonDocument("_id", "null-one")).First()["email"].IsBsonNull);
        Assert.DoesNotContain("body", document.Names, StringComparer.Ordinal);
        Assert.DoesNotContain("envelope", document.Names, StringComparer.Ordinal);
    }

    [SkippableFact]
    public void Composite_keys_use_an_ordered_native_id_subdocument()
    {
        using var connection = OpenConnection();
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("p1-composite-" + Guid.NewGuid().ToString("N")),
            Name = "P1Composite_" + Guid.NewGuid().ToString("N"),
            Columns =
            [
                new() { Name = "region", Type = PortableType.String, IsNullable = false },
                new() { Name = "customerNo", Type = PortableType.Int64, IsNullable = false },
                new() { Name = "name", Type = PortableType.String }
            ],
            Key = new KeyDefinition { Columns = ["region", "customerNo"] }
        };
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, MongoStorageAccess.Global);
        session.Insert(new MongoStorageValues(new Dictionary<string, object?>
        {
            ["region"] = "west",
            ["customerNo"] = 7L,
            ["name"] = "Ada"
        }));

        var document = Assert.IsType<MongoDbProviderConnection>(connection).Database
            .GetCollection<BsonDocument>(unit.Name).Find(FilterDefinition<BsonDocument>.Empty).First();
        var id = document["_id"].AsBsonDocument;
        Assert.Equal(["region", "customerNo"], id.Names);
        Assert.Equal("west", id["region"].AsString);
        Assert.Equal(7L, id["customerNo"].AsInt64);
    }

    [Fact]
    public void Mongo_keeps_only_its_physical_key_rename_guard_beside_shared_planning()
    {
        var initial = new StorageUnit
        {
            Id = new StorageUnitId("mongo-key-layout"),
            Name = "mongo_key_layout",
            Columns =
            [
                new ColumnDefinition { Name = "region", Type = PortableType.String, IsNullable = false },
                new ColumnDefinition { Name = "number", Type = PortableType.Int64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["region", "number"] }
        };
        var renamed = initial with
        {
            Columns = [.. initial.Columns.Select(column => column.Name == "region"
                ? column with { Id = "region", Name = "area" }
                : column)],
            Key = new KeyDefinition { Columns = ["area", "number"] }
        };
        var reordered = initial with { Key = new KeyDefinition { Columns = ["number", "region"] } };

        var providerRefusal = Assert.Single(MongoDeclarationRules.StableDeclarationRefusals(initial, renamed));

        Assert.Equal("GW-PORT-008", providerRefusal.Code);
        Assert.Empty(MongoDeclarationRules.StableDeclarationRefusals(initial, reordered));
    }

    [SkippableFact]
    public void Composite_key_reordering_is_refused_after_reopening_the_provider()
    {
        var connectionString = LiveMongo.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB integration tests.");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("p1-reorder-" + Guid.NewGuid().ToString("N")),
            Name = "P1Reorder_" + Guid.NewGuid().ToString("N"),
            Columns =
            [
                new() { Name = "region", Type = PortableType.String, IsNullable = false },
                new() { Name = "customerNo", Type = PortableType.Int64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["region", "customerNo"] }
        };
        using (var first = new MongoDbProviderFactory().Create(connectionString!))
            first.Schema.Apply(unit);

        var reordered = unit with { Key = new KeyDefinition { Columns = ["customerNo", "region"] } };
        using var reopened = new MongoDbProviderFactory().Create(connectionString!);
        var refusal = Assert.Throws<PhysicalSchemaPlanRefusedException>(() => reopened.Schema.Apply(reordered));

        Assert.Contains("GW-SCHEMA-015", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("logical key identity or column order", refusal.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Admission_refuses_documents_missing_declared_columns_with_a_backfill_command()
    {
        using var connection = OpenConnection();
        var unit = TestUnits.Customer with
        {
            Id = new StorageUnitId("p1-admission-" + Guid.NewGuid().ToString("N")),
            Name = "P1Admission_" + Guid.NewGuid().ToString("N")
        };
        connection.Schema.Apply(unit);
        Assert.IsType<MongoDbProviderConnection>(connection).Database
            .GetCollection<BsonDocument>(unit.Name)
            .InsertOne(new BsonDocument("_id", "legacy"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            connection.OpenSession(unit, MongoStorageAccess.Global));

        var report = connection.InspectSchema(unit, MongoStorageAccess.Global);
        Assert.Contains(report.ColumnDrift, refusal => refusal.Code == "GW-RUNTIME-001" &&
            refusal.Path == "columns.id");
        Assert.False(report.IsProcessReady);

        Assert.Contains("backfill", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(unit.Name, exception.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Mongo_index_drift_is_classified_without_blocking_store_open()
    {
        using var connection = OpenConnection();
        var unit = TestUnits.Customer with
        {
            Id = new StorageUnitId("p1-index-admission-" + Guid.NewGuid().ToString("N")),
            Name = "P1IndexAdmission_" + Guid.NewGuid().ToString("N")
        };
        connection.Schema.Apply(unit);
        var native = Assert.IsType<MongoDbProviderConnection>(connection).Database
            .GetCollection<BsonDocument>(unit.Name);
        native.Indexes.DropOne("unique_email");

        var report = connection.InspectSchema(unit, MongoStorageAccess.Global);

        Assert.True(report.IsProcessReady);
        Assert.Contains(report.IndexDrift, refusal => refusal.Code == "GW-RUNTIME-002" &&
            refusal.Path == "indexes.unique_email");
        var session = connection.OpenSession(unit, MongoStorageAccess.Global);
        Assert.NotNull(session);
    }

    [SkippableFact]
    public void Folded_unique_index_migration_backfills_before_index_and_reports_fold_collision()
    {
        using var connection = OpenConnection();
        var original = new StorageUnit
        {
            Id = new StorageUnitId("q9-mongo-folded-" + Guid.NewGuid().ToString("N")),
            Name = "Q9MongoFolded_" + Guid.NewGuid().ToString("N"),
            Columns =
            [
                new() { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new() { Name = "status", Type = PortableType.String, MaxLength = 32, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        connection.Schema.Apply(original);
        var session = connection.OpenSession(original, MongoStorageAccess.Global);
        Assert.Equal(MongoWriteOutcomeStatus.Inserted, session.Insert(new MongoStorageValues(
            new Dictionary<string, object?> { ["id"] = 1, ["status"] = "Open" })).Status);
        Assert.Equal(MongoWriteOutcomeStatus.Inserted, session.Insert(new MongoStorageValues(
            new Dictionary<string, object?> { ["id"] = 2, ["status"] = "open" })).Status);

        var folded = original with
        {
            Columns = [.. original.Columns.Select(column => column.Name == "status"
                ? column with { Collation = PortableCollation.OrdinalIgnoreCase }
                : column)],
            Indexes = [new IndexDefinition { Name = "unique_status", Columns = [new IndexColumn("status")], IsUnique = true }]
        };

        var exception = Assert.Throws<MongoCommandException>(() => connection.Schema.Apply(folded));
        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public void Folded_unique_index_migration_backfills_existing_data_before_successful_index_creation()
    {
        using var connection = OpenConnection();
        var original = new StorageUnit
        {
            Id = new StorageUnitId("q9-mongo-folded-success-" + Guid.NewGuid().ToString("N")),
            Name = "Q9MongoFoldedSuccess_" + Guid.NewGuid().ToString("N"),
            Columns =
            [
                new() { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new() { Name = "status", Type = PortableType.String, MaxLength = 32, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        connection.Schema.Apply(original);
        var session = connection.OpenSession(original, MongoStorageAccess.Global);
        Assert.Equal(MongoWriteOutcomeStatus.Inserted, session.Insert(new MongoStorageValues(
            new Dictionary<string, object?> { ["id"] = 1, ["status"] = "Open" })).Status);

        var folded = original with
        {
            Columns = [.. original.Columns.Select(column => column.Name == "status"
                ? column with { Collation = PortableCollation.OrdinalIgnoreCase }
                : column)],
            Indexes = [new IndexDefinition { Name = "unique_status", Columns = [new IndexColumn("status")], IsUnique = true }]
        };

        var applied = connection.Schema.Apply(folded);
        Assert.True(applied.Applied);
        var status = new ColumnRef(new TableId(folded.Name), "status", QueryType.String, false, 32,
            stringComparison: QueryStringComparisonPolicy.AsciiIgnoreCase);
        var result = connection.OpenSession(folded, MongoStorageAccess.Global).Query(new QueryRequest(
            new TableId(folded.Name), new Predicate.StartsWith(status, "OP"), [], Projection.All, Paging.None));
        Assert.Equal([1], result.Rows.Select(row => Assert.IsType<int>(row["id"])));
        Assert.Equal(MongoWriteOutcomeStatus.UniqueViolation, connection.OpenSession(folded, MongoStorageAccess.Global)
            .Insert(new MongoStorageValues(new Dictionary<string, object?> { ["id"] = 2, ["status"] = "open" })).Status);
    }

    [SkippableFact]
    public void Legacy_folded_algorithm_cache_drift_is_ignored_when_history_is_authoritative()
    {
        using var connection = OpenConnection();
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("q9-mongo-drift-" + Guid.NewGuid().ToString("N")),
            Name = "Q9MongoDrift_" + Guid.NewGuid().ToString("N"),
            Columns =
            [
                new() { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new() { Name = "status", Type = PortableType.String, MaxLength = 32, IsNullable = false, Collation = PortableCollation.OrdinalIgnoreCase }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        connection.Schema.Apply(unit);
        var metadata = Assert.IsType<MongoDbProviderConnection>(connection).Database
            .GetCollection<BsonDocument>("__groundwork_metadata");
        Assert.Equal(0, metadata.CountDocuments(new BsonDocument("_id", "schema:" + unit.Id.Value)));
        var derivedName = SearchKeyProjection.ColumnName("status");
        metadata.ReplaceOne(
            new BsonDocument("_id", "schema:" + unit.Id.Value),
            new BsonDocument
            {
                ["_id"] = "schema:" + unit.Id.Value,
                ["collection"] = unit.Name,
                ["fingerprint"] = SchemaIdentity.Fingerprint(unit),
                ["derived"] = new BsonArray
                {
                    new BsonDocument
                    {
                        ["name"] = derivedName,
                        ["algorithmId"] = "stale-search-key-v0"
                    }
                }
            },
            new ReplaceOptions { IsUpsert = true });
        Assert.Equal("stale-search-key-v0", metadata.Find(new BsonDocument("_id", "schema:" + unit.Id.Value))
            .First()["derived"][0]["algorithmId"].AsString);

        var session = connection.OpenSession(unit, MongoStorageAccess.Global);
        Assert.Equal(unit.Name, session.Unit.Name);
        var report = connection.InspectSchema(unit, MongoStorageAccess.Global);
        Assert.DoesNotContain(report.ColumnDrift, refusal => refusal.Path.EndsWith("searchKeyAlgorithm", StringComparison.Ordinal));
    }

    [SkippableFact]
    public void Folded_partial_updates_preserve_keys_through_aggregate_exact_and_fallback_batches()
    {
        using var connection = OpenConnection();
        var unit = RequiredFoldedUnit("q9-mongo-batch-folded");
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, MongoStorageAccess.Global);
        Assert.Equal(MongoWriteOutcomeStatus.Inserted, session.Insert(new MongoStorageValues(
            new Dictionary<string, object?> { ["id"] = 1, ["status"] = "Open" })).Status);
        var stored = session.Read(new MongoStorageKey(new Dictionary<string, object?> { ["id"] = 1 }));
        Assert.NotNull(stored);
        Assert.DoesNotContain(SearchKeyProjection.ColumnName("status"), stored.Values.Values.Keys);

        var aggregateObserver = new ProviderCommandObserver();
        var batch = Assert.IsAssignableFrom<IBatchedStorageSession>(
            connection.OpenSession(unit, MongoStorageAccess.Global, aggregateObserver));
        var aggregate = batch.ApplyBatch(
            [RowWrite.Upsert(unit, new StorageValues(new Dictionary<string, object?> { ["id"] = 1 }))]);
        Assert.Equal(WriteOutcomeStatus.Upserted, Assert.Single(aggregate).Outcome.Status);
        Assert.Contains(aggregateObserver.Commands, command => command.Operation == "mongodb.batch-write");

        var exact = batch.ApplyBatch(
            [RowWrite.Upsert(unit, new StorageValues(new Dictionary<string, object?> { ["id"] = 1 }))],
            exactOutcomes: true);
        Assert.Equal(WriteOutcomeStatus.Updated, Assert.Single(exact).Outcome.Status);

        var fallback = batch.ApplyBatch(
            [RowWrite.Update(unit, new StorageValues(new Dictionary<string, object?> { ["id"] = 1 }))]);
        Assert.Equal(WriteOutcomeStatus.Updated, Assert.Single(fallback).Outcome.Status);

        var missing = Assert.Throws<InvalidOperationException>(() => batch.ApplyBatch(
            [RowWrite.Upsert(unit, new StorageValues(new Dictionary<string, object?> { ["id"] = 2 }))]));
        Assert.Contains("status", missing.Message, StringComparison.Ordinal);
        Assert.Null(session.Read(new MongoStorageKey(new Dictionary<string, object?> { ["id"] = 2 })));

        var status = new ColumnRef(new TableId(unit.Name), "status", QueryType.String, false, 32,
            stringComparison: QueryStringComparisonPolicy.AsciiIgnoreCase);
        var result = session.Query(new QueryRequest(new TableId(unit.Name),
            new Predicate.StartsWith(status, "OP"), [], Projection.All, Paging.None));
        Assert.Equal([1], result.Rows.Select(row => Assert.IsType<int>(row["id"])));
    }

    [SkippableFact]
    public void Folded_partial_conditional_upserts_preserve_existing_values_and_explicit_preconditions()
    {
        using var connection = OpenConnection();
        var unit = RequiredFoldedUnit(
            "q9-mongo-conditional-folded",
            concurrency: ConcurrencyDeclaration.Optimistic());
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, MongoStorageAccess.Global);
        Assert.Equal(1, session.Insert(new MongoStorageValues(
            new Dictionary<string, object?> { ["id"] = 1, ["status"] = "Open" })).Version);

        var updated = session.ConditionalUpsert(
            new MongoStorageValues(new Dictionary<string, object?> { ["id"] = 1 }),
            MongoWriteOptions.IfVersion(1));

        Assert.Equal(MongoWriteOutcomeStatus.Updated, updated.Status);
        Assert.Equal(2, updated.Version);
        Assert.Equal("Open", session.Read(new MongoStorageKey(
            new Dictionary<string, object?> { ["id"] = 1 }))!.Values.Values["status"]);
        var fallback = Assert.IsAssignableFrom<IBatchedStorageSession>(session).ApplyBatch(
            [RowWrite.Upsert(unit, new StorageValues(new Dictionary<string, object?> { ["id"] = 1 }),
                WriteOptions.IfVersion(2))]);
        Assert.Equal(WriteOutcomeStatus.Updated, Assert.Single(fallback).Outcome.Status);
        Assert.Equal(3, fallback[0].Outcome.Version);
        Assert.Equal("Open", session.Read(new MongoStorageKey(
            new Dictionary<string, object?> { ["id"] = 1 }))!.Values.Values["status"]);
        Assert.Equal(MongoWriteOutcomeStatus.ConcurrencyConflict, session.ConditionalUpsert(
            new MongoStorageValues(new Dictionary<string, object?> { ["id"] = 1 }),
            MongoWriteOptions.IfVersion(1)).Status);
        Assert.Equal(MongoWriteOutcomeStatus.ConcurrencyConflict, session.ConditionalUpsert(
            new MongoStorageValues(new Dictionary<string, object?> { ["id"] = 2 }),
            MongoWriteOptions.IfVersion(1)).Status);
        Assert.Null(session.Read(new MongoStorageKey(new Dictionary<string, object?> { ["id"] = 2 })));

        var missingRequired = Assert.Throws<InvalidOperationException>(() => session.ConditionalUpsert(
            new MongoStorageValues(new Dictionary<string, object?> { ["id"] = 3 })));
        Assert.Contains("status", missingRequired.Message, StringComparison.Ordinal);
        Assert.Null(session.Read(new MongoStorageKey(new Dictionary<string, object?> { ["id"] = 3 })));
    }

    [SkippableFact]
    public void Exact_batch_inline_version_is_authoritative_for_conditional_delete()
    {
        using var connection = OpenConnection();
        var unit = RequiredFoldedUnit(
            "mongo-exact-delete-version",
            concurrency: ConcurrencyDeclaration.Optimistic());
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, MongoStorageAccess.Global);
        Assert.Equal(1, session.Insert(new MongoStorageValues(
            new Dictionary<string, object?> { ["id"] = 1, ["status"] = "Open" })).Version);

        var exact = Assert.IsAssignableFrom<IBatchedStorageSession>(session).ApplyBatch(
            [RowWrite.Upsert(unit,
                new StorageValues(new Dictionary<string, object?> { ["id"] = 1, ["status"] = "Leased" }),
                WriteOptions.IfVersion(1))],
            exactOutcomes: true);
        Assert.Equal(2, Assert.Single(exact).Outcome.Version);

        var deleted = session.Delete(
            new MongoStorageKey(new Dictionary<string, object?> { ["id"] = 1 }),
            MongoWriteOptions.IfVersion(2));

        Assert.Equal(MongoWriteOutcomeStatus.Deleted, deleted.Status);
        Assert.Equal(2, deleted.Version);
        Assert.Null(session.Read(new MongoStorageKey(new Dictionary<string, object?> { ["id"] = 1 })));
    }

    [SkippableFact]
    public void Folded_aggregate_batch_does_not_report_an_unmatched_incomplete_upsert_as_success()
    {
        using var connection = OpenConnection();
        var unit = RequiredFoldedUnit("q9-mongo-aggregate-folded", uniqueStatus: true);
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, MongoStorageAccess.Global);
        Assert.Equal(MongoWriteOutcomeStatus.Inserted, session.Insert(new MongoStorageValues(
            new Dictionary<string, object?> { ["id"] = 1, ["status"] = "Open" })).Status);

        var batch = Assert.IsAssignableFrom<IBatchedStorageSession>(session);
        var missingRequired = Assert.Throws<InvalidOperationException>(() => batch.ApplyBatch(
        [
            RowWrite.Upsert(unit, new StorageValues(new Dictionary<string, object?> { ["id"] = 2 })),
            RowWrite.Upsert(unit, new StorageValues(new Dictionary<string, object?> { ["id"] = 3, ["status"] = "OPEN" }))
        ]));

        Assert.Contains("status", missingRequired.Message, StringComparison.Ordinal);
        Assert.Null(session.Read(new MongoStorageKey(new Dictionary<string, object?> { ["id"] = 2 })));
        Assert.Null(session.Read(new MongoStorageKey(new Dictionary<string, object?> { ["id"] = 3 })));
    }

    [SkippableFact]
    public void StartsWithUsesIndex_for_the_optimizer_selected_folded_physical_index_without_a_hint()
    {
        var connectionString = LiveMongo.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB explain proofs.");
        var previousFlag = Environment.GetEnvironmentVariable("GW_EXPLAIN_ASSERT");
        var previousDirectory = Environment.GetEnvironmentVariable("GW_EXPLAIN_ARTIFACT_DIR");
        var artifactDirectory = Path.Combine(Path.GetTempPath(), "groundwork-q11-mongo-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("GW_EXPLAIN_ASSERT", "1");
        Environment.SetEnvironmentVariable("GW_EXPLAIN_ARTIFACT_DIR", artifactDirectory);
        try
        {
            using var connection = new MongoDbProviderFactory().Create(connectionString!);
            var unit = new StorageUnit
            {
                Id = new StorageUnitId("q9-mongo-explain-" + Guid.NewGuid().ToString("N")),
                Name = "Q9MongoExplain_" + Guid.NewGuid().ToString("N"),
                Columns =
                [
                    new() { Name = "id", Type = PortableType.Int32, IsNullable = false },
                    new() { Name = "status", Type = PortableType.String, MaxLength = 32, IsNullable = false, Collation = PortableCollation.OrdinalIgnoreCase }
                ],
                Key = new KeyDefinition { Columns = ["id"] },
                Indexes = [new IndexDefinition { Name = "by_status", Columns = [new IndexColumn("status")] }]
            };
            connection.Schema.Apply(unit);
            var session = connection.OpenSession(unit, MongoStorageAccess.Global);
            for (var id = 1; id <= 2_000; id++)
            {
                session.Insert(new MongoStorageValues(new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["status"] = id == 1 ? "Open" : "other-" + id
                }));
            }

            var table = new TableId(unit.Name);
            var status = new ColumnRef(table, "status", QueryType.String, false, 32,
                stringComparison: QueryStringComparisonPolicy.AsciiIgnoreCase);
            var options = new QueryRenderOptions(
                [new QueryIndexDeclaration("by_status", [new QueryIndexColumn("status", false, QueryType.String)], QueryIndexPinning.ProviderDefault)],
                selectedIndex: "by_status");
            var result = session.Query(new QueryRequest(table,
                new Predicate.StartsWith(status, "OP"), [], Projection.All, Paging.None), options);

            Assert.Equal("by_status", result.SelectedIndex);
            Assert.Equal([1], result.Rows.Select(row => Assert.IsType<int>(row["id"])));
            var artifact = Assert.Single(Directory.GetFiles(artifactDirectory, "*.json"));
            Assert.Contains("optimizer-selected", Path.GetFileName(artifact), StringComparison.Ordinal);
            var plan = File.ReadAllText(artifact);
            Assert.Contains("IXSCAN", plan, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(SearchKeyProjection.ColumnName("status"), plan, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GW_EXPLAIN_ASSERT", previousFlag);
            Environment.SetEnvironmentVariable("GW_EXPLAIN_ARTIFACT_DIR", previousDirectory);
            if (Directory.Exists(artifactDirectory))
                Directory.Delete(artifactDirectory, recursive: true);
        }
    }

    [SkippableFact]
    public void Composite_declared_key_prefix_uses_the_Mongo_native_index_without_a_hint()
    {
        var connectionString = LiveMongo.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB explain proofs.");
        var previousFlag = Environment.GetEnvironmentVariable("GW_EXPLAIN_ASSERT");
        var previousDirectory = Environment.GetEnvironmentVariable("GW_EXPLAIN_ARTIFACT_DIR");
        var artifactDirectory = Path.Combine(Path.GetTempPath(), "groundwork-key-mongo-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("GW_EXPLAIN_ASSERT", "1");
        Environment.SetEnvironmentVariable("GW_EXPLAIN_ARTIFACT_DIR", artifactDirectory);
        try
        {
            using var connection = new MongoDbProviderFactory().Create(connectionString!);
            var unit = new StorageUnit
            {
                Id = new StorageUnitId("mongo-key-explain-" + Guid.NewGuid().ToString("N")),
                Name = "MongoKeyExplain_" + Guid.NewGuid().ToString("N"),
                Columns =
                [
                    new() { Name = "tenant", Type = PortableType.String, MaxLength = 64, IsNullable = false },
                    new() { Name = "id", Type = PortableType.Int32, IsNullable = false },
                    new() { Name = "payload", Type = PortableType.String, MaxLength = 64, IsNullable = false }
                ],
                Key = new KeyDefinition { Columns = ["tenant", "id"] }
            };
            Assert.True(connection.Schema.Apply(unit).Applied);
            var session = connection.OpenSession(unit, MongoStorageAccess.Global);
            for (var id = 1; id <= 2_000; id++)
            {
                session.Insert(new MongoStorageValues(new Dictionary<string, object?>
                {
                    ["tenant"] = id == 1 ? "selected" : "tenant-" + id,
                    ["id"] = id,
                    ["payload"] = "value-" + id
                }));
            }

            var table = new TableId(unit.Name);
            var tenant = new ColumnRef(table, "tenant", QueryType.String, false, 64);
            const string logicalKey = MongoSchemaTargets.DeclaredKeyCoverageIndexName;
            var options = new QueryRenderOptions(
                [new QueryIndexDeclaration(logicalKey,
                [
                    new QueryIndexColumn("tenant", false, QueryType.String),
                    new QueryIndexColumn("id", false, QueryType.Int32)
                ], QueryIndexPinning.ProviderDefault)],
                selectedIndex: logicalKey);
            var result = session.Query(new QueryRequest(table,
                new Predicate.Equal(tenant, QueryConstant.Of(tenant, "selected")),
                [], Projection.All, Paging.None), options);

            Assert.Equal(logicalKey, result.SelectedIndex);
            Assert.Equal(1, Assert.Single(result.Rows)["id"]);
            var artifact = Assert.Single(Directory.GetFiles(artifactDirectory, "*.json"));
            Assert.Contains("optimizer-selected", Path.GetFileName(artifact), StringComparison.Ordinal);
            var plan = File.ReadAllText(artifact);
            Assert.Contains("IXSCAN", plan, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(MongoSchemaTargets.DeclaredKeyIndexName, plan, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GW_EXPLAIN_ASSERT", previousFlag);
            Environment.SetEnvironmentVariable("GW_EXPLAIN_ARTIFACT_DIR", previousDirectory);
            if (Directory.Exists(artifactDirectory))
                Directory.Delete(artifactDirectory, recursive: true);
        }
    }

    [SkippableFact]
    public void Mongo_bson_type_drift_names_the_column_without_confusing_it_with_index_drift()
    {
        using var connection = OpenConnection();
        var unit = TestUnits.Customer with
        {
            Id = new StorageUnitId("p1-type-admission-" + Guid.NewGuid().ToString("N")),
            Name = "P1TypeAdmission_" + Guid.NewGuid().ToString("N")
        };
        connection.Schema.Apply(unit);
        Assert.IsType<MongoDbProviderConnection>(connection).Database
            .GetCollection<BsonDocument>(unit.Name)
            .InsertOne(new BsonDocument
            {
                ["_id"] = "bad-type",
                ["id"] = 42,
                ["name"] = "Ada",
                ["email"] = "bad-type@example.test",
                ["createdAt"] = DateTime.UtcNow,
                ["isActive"] = true,
                ["balance"] = new BsonDecimal128(1m)
            });

        var report = connection.InspectSchema(unit, MongoStorageAccess.Global);

        Assert.Contains(report.ColumnDrift, refusal => refusal.Code == "GW-RUNTIME-001" &&
            refusal.Path == "columns.id.type");
        Assert.DoesNotContain(report.IndexDrift, refusal => refusal.Path == "columns.id.type");
        Assert.False(report.IsProcessReady);
    }

    [SkippableFact]
    public void Mongo_json_admission_accepts_scalars_containers_and_json_null_for_required_columns()
    {
        using var connection = OpenConnection();
        var nullableUnit = JsonAdmissionUnit("mongo-json-admission-" + Guid.NewGuid().ToString("N"), nullable: true);
        var database = Assert.IsType<MongoDbProviderConnection>(connection).Database;
        database.CreateCollection(nullableUnit.Name);
        database.GetCollection<BsonDocument>(nullableUnit.Name).InsertMany(
        [
            new BsonDocument { ["_id"] = "object", ["id"] = "object", ["payload"] = new BsonDocument("kind", "object") },
            new BsonDocument { ["_id"] = "array", ["id"] = "array", ["payload"] = new BsonArray { 1, "two" } },
            new BsonDocument { ["_id"] = "string", ["id"] = "string", ["payload"] = "text" },
            new BsonDocument { ["_id"] = "int", ["id"] = "int", ["payload"] = 42 },
            new BsonDocument { ["_id"] = "long", ["id"] = "long", ["payload"] = 2147483648L },
            new BsonDocument { ["_id"] = "double", ["id"] = "double", ["payload"] = 1.5 },
            new BsonDocument { ["_id"] = "decimal", ["id"] = "decimal", ["payload"] = new BsonDecimal128(1.25m) },
            new BsonDocument { ["_id"] = "boolean", ["id"] = "boolean", ["payload"] = true },
            new BsonDocument { ["_id"] = "null", ["id"] = "null", ["payload"] = BsonNull.Value }
        ]);

        Assert.True(connection.Schema.Apply(nullableUnit).Applied);
        var accepted = connection.InspectSchema(nullableUnit, MongoStorageAccess.Global);
        Assert.True(accepted.IsProcessReady);
        Assert.Empty(accepted.ColumnDrift);

        database.GetCollection<BsonDocument>(nullableUnit.Name).InsertOne(
            new BsonDocument { ["_id"] = "date", ["id"] = "date", ["payload"] = new BsonDateTime(DateTime.UtcNow) });
        var coordinatorDrift = connection.InspectSchema(nullableUnit, MongoStorageAccess.Global);
        var coordinatorRefusal = Assert.Single(coordinatorDrift.ColumnDrift,
            refusal => refusal.Path == "columns.payload.type");
        Assert.Contains("one of the accepted BSON types", coordinatorRefusal.Message, StringComparison.Ordinal);
        Assert.Contains("'object'", coordinatorRefusal.Message, StringComparison.Ordinal);

        var requiredUnit = JsonAdmissionUnit("mongo-json-required-" + Guid.NewGuid().ToString("N"), nullable: false);
        database.CreateCollection(requiredUnit.Name);
        database.GetCollection<BsonDocument>(requiredUnit.Name).InsertOne(
            new BsonDocument { ["_id"] = "null", ["id"] = "null", ["payload"] = BsonNull.Value });

        Assert.True(connection.Schema.Apply(requiredUnit).Applied);
        var required = connection.InspectSchema(requiredUnit, MongoStorageAccess.Global);
        Assert.True(required.IsProcessReady);
        Assert.Empty(required.ColumnDrift);

        var invalidUnit = JsonAdmissionUnit("mongo-json-invalid-" + Guid.NewGuid().ToString("N"), nullable: false);
        database.CreateCollection(invalidUnit.Name);
        database.GetCollection<BsonDocument>(invalidUnit.Name).InsertOne(
            new BsonDocument { ["_id"] = "date", ["id"] = "date", ["payload"] = new BsonDateTime(DateTime.UtcNow) });

        var refusal = Assert.Throws<InvalidOperationException>(() => connection.Schema.Apply(invalidUnit));
        Assert.Contains("one of the accepted BSON types", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("'object'", refusal.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Scope_and_optimistic_concurrency_are_provider_behaviors()
    {
        using var connection = OpenConnection();
        var unit = TestUnits.Customer with
        {
            Id = new StorageUnitId("p1-scope-" + Guid.NewGuid().ToString("N")),
            Name = "P1Scope_" + Guid.NewGuid().ToString("N"),
            Scope = ScopePolicy.Scoped,
            Concurrency = ConcurrencyDeclaration.Optimistic()
        };
        connection.Schema.Apply(unit);
        var first = connection.OpenSession(unit, MongoStorageAccess.Scoped(new StorageScope("a")));
        var second = connection.OpenSession(unit, MongoStorageAccess.Scoped(new StorageScope("b")));

        var inserted = first.Insert(CustomerValues("same", null));
        Assert.Equal(1, inserted.Version);
        Assert.Equal("Ada", first.Read(Key("same"))!.Values.Values["name"]);
        Assert.Null(second.Read(Key("same")));
        Assert.Equal(MongoWriteOutcomeStatus.ConcurrencyConflict,
            first.Update(CustomerValues("same", null), MongoWriteOptions.IfVersion(9)).Status);
        Assert.Equal(MongoWriteOutcomeStatus.Updated,
            first.Update(CustomerValues("same", null), MongoWriteOptions.IfVersion(1)).Status);
    }

    [SkippableTheory]
    [InlineData(ScopePolicy.Global)]
    [InlineData(ScopePolicy.Scoped)]
    public void Optimistic_upsert_classifies_insert_update_and_stale_cas_for_each_scope(ScopePolicy scope)
    {
        using var connection = OpenConnection();
        var unit = OptimisticUpsertUnit("optimistic-upsert", scope);
        Assert.True(connection.Schema.Apply(unit).Applied);
        var access = scope == ScopePolicy.Scoped
            ? MongoStorageAccess.Scoped(new StorageScope("tenant-a"))
            : MongoStorageAccess.Global;
        var session = connection.OpenSession(unit, access);

        var inserted = session.Upsert(OptimisticUpsertValues("one", "first"));
        Assert.Equal(MongoWriteOutcomeStatus.Upserted, inserted.Status);
        Assert.Equal(1, inserted.Version);

        var updated = session.Upsert(OptimisticUpsertValues("one", "second"));
        Assert.Equal(MongoWriteOutcomeStatus.Upserted, updated.Status);
        Assert.Equal(2, updated.Version);
        Assert.Equal("second", session.Read(new MongoStorageKey(
            new Dictionary<string, object?> { ["id"] = "one" }))!.Values.Values["payload"]);

        var stale = session.Upsert(
            OptimisticUpsertValues("one", "stale"), MongoWriteOptions.IfVersion(1));
        Assert.Equal(MongoWriteOutcomeStatus.ConcurrencyConflict, stale.Status);
        Assert.Equal(2, stale.Version);
        Assert.Equal("second", session.Read(new MongoStorageKey(
            new Dictionary<string, object?> { ["id"] = "one" }))!.Values.Values["payload"]);

        var missingWithCas = session.Upsert(
            OptimisticUpsertValues("two", "cas"), MongoWriteOptions.IfVersion(1));
        Assert.Equal(MongoWriteOutcomeStatus.ConcurrencyConflict, missingWithCas.Status);
        Assert.Null(session.Read(new MongoStorageKey(
            new Dictionary<string, object?> { ["id"] = "two" })));
    }

    [SkippableFact]
    public void Optimistic_upsert_classification_is_preserved_inside_a_transaction()
    {
        using var connection = OpenConnection();
        Skip.If(connection.ProviderSequenceFit is ProviderFit.Unsupported,
            "MongoDB standalone deployments cannot execute unit-of-work transactions.");
        var unit = OptimisticUpsertUnit("optimistic-upsert-transaction");
        Assert.True(connection.Schema.Apply(unit).Applied);

        using var work = connection.BeginUnitOfWork(MongoStorageAccess.Global, unit);
        var session = work.OpenSession(unit);
        var inserted = session.Upsert(OptimisticUpsertValues("one", "first"));
        var updated = session.Upsert(OptimisticUpsertValues("one", "second"));
        var stale = session.Upsert(
            OptimisticUpsertValues("one", "stale"), MongoWriteOptions.IfVersion(1));

        Assert.Equal(MongoWriteOutcomeStatus.Upserted, inserted.Status);
        Assert.Equal(1, inserted.Version);
        Assert.Equal(MongoWriteOutcomeStatus.Upserted, updated.Status);
        Assert.Equal(2, updated.Version);
        Assert.Equal(MongoWriteOutcomeStatus.ConcurrencyConflict, stale.Status);
        Assert.Equal(2, stale.Version);
        Assert.Equal("second", session.Read(new MongoStorageKey(
            new Dictionary<string, object?> { ["id"] = "one" }))!.Values.Values["payload"]);
        work.Commit();
    }

    [SkippableTheory]
    [InlineData(ScopePolicy.Global)]
    [InlineData(ScopePolicy.Scoped)]
    public void Optimistic_aggregate_and_exact_batches_classify_insert_and_update(ScopePolicy scope)
    {
        using var connection = OpenConnection();
        var unit = OptimisticUpsertUnit("optimistic-upsert-batch", scope);
        Assert.True(connection.Schema.Apply(unit).Applied);
        var access = scope == ScopePolicy.Scoped
            ? MongoStorageAccess.Scoped(new StorageScope("tenant-a"))
            : MongoStorageAccess.Global;
        var session = connection.OpenSession(unit, access);
        var batch = Assert.IsAssignableFrom<IBatchedStorageSession>(session);

        var aggregateInsert = Assert.Single(batch.ApplyBatch(
            [RowWrite.Upsert(unit, OptimisticUpsertStoreValues("one", "first"))]));
        Assert.Equal(WriteOutcomeStatus.Upserted, aggregateInsert.Outcome.Status);
        Assert.Equal(1, session.Read(new MongoStorageKey(
            new Dictionary<string, object?> { ["id"] = "one" }))!.Version);

        var aggregateUpdate = Assert.Single(batch.ApplyBatch(
            [RowWrite.Upsert(unit, OptimisticUpsertStoreValues("one", "second"))]));
        Assert.Equal(WriteOutcomeStatus.Upserted, aggregateUpdate.Outcome.Status);
        Assert.Equal(2, session.Read(new MongoStorageKey(
            new Dictionary<string, object?> { ["id"] = "one" }))!.Version);

        var exactInsert = Assert.Single(batch.ApplyBatch(
            [RowWrite.Upsert(unit, OptimisticUpsertStoreValues("two", "first"))],
            exactOutcomes: true));
        Assert.Equal(WriteOutcomeStatus.Inserted, exactInsert.Outcome.Status);
        Assert.Equal(1, exactInsert.Outcome.Version);

        var exactUpdate = Assert.Single(batch.ApplyBatch(
            [RowWrite.Upsert(unit, OptimisticUpsertStoreValues("two", "second"))],
            exactOutcomes: true));
        Assert.Equal(WriteOutcomeStatus.Updated, exactUpdate.Outcome.Status);
        Assert.Equal(2, exactUpdate.Outcome.Version);

        var stale = Assert.Single(batch.ApplyBatch(
            [RowWrite.Upsert(unit, OptimisticUpsertStoreValues("two", "stale"), WriteOptions.IfVersion(1))]));
        Assert.Equal(WriteOutcomeStatus.ConcurrencyConflict, stale.Outcome.Status);
        Assert.Equal("second", session.Read(new MongoStorageKey(
            new Dictionary<string, object?> { ["id"] = "two" }))!.Values.Values["payload"]);
    }

    [SkippableFact]
    public async Task Concurrent_transactional_create_only_reservations_report_a_conflict_instead_of_leaking_wiredtiger_error()
    {
        var connectionString = LiveMongo.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB integration tests.");
        using var nativeSetup = new MongoDbProviderFactory().Create(connectionString!);
        Skip.If(nativeSetup.ProviderSequenceFit is ProviderFit.Unsupported,
            "MongoDB standalone deployments cannot execute unit-of-work transactions.");

        var name = "mongo_login_race_" + Guid.NewGuid().ToString("N")[..20];
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns =
            [
                new() { Name = "identity", Type = PortableType.String, IsNullable = false, MaxLength = 128 },
                new() { Name = "owner", Type = PortableType.String, IsNullable = false, MaxLength = 128 }
            ],
            Key = new KeyDefinition { Columns = ["identity"] },
            Concurrency = ConcurrencyDeclaration.Optimistic()
        };
        Assert.True(nativeSetup.Schema.Apply(unit).Applied);

        using var firstConnection = new MongoProviderFactory().Create(connectionString!);
        using var secondConnection = new MongoProviderFactory().Create(connectionString!);
        using var first = firstConnection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit);
        using var second = secondConnection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit);
        first.Stage(RowWrite.ConditionalUpsert(unit, ReservationValues("first"), WriteOptions.CreateOnly));
        second.Stage(RowWrite.ConditionalUpsert(unit, ReservationValues("second"), WriteOptions.CreateOnly));

        using var start = new Barrier(2);
        var firstTask = Task.Run(() => CommitReservation(first, start));
        var secondTask = Task.Run(() => CommitReservation(second, start));
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Single(results, result => result.Report?.IsSuccessful == true);
        var loser = Assert.Single(results, result => result.Report is null);
        var batchError = Assert.IsType<BatchWriteException>(loser.Error);
        Assert.Equal(WriteOutcomeStatus.ConcurrencyConflict, Assert.Single(batchError.Outcomes).Outcome.Status);
        var winner = firstConnection.OpenSession(unit, StorageAccess.Global).Read(new StorageKey(
            new Dictionary<string, object?> { ["identity"] = "provider|subject" }));
        Assert.NotNull(winner);
        Assert.True(winner!.Values.Values["owner"] is "first" or "second");
        Assert.Equal(1, winner.Version);

        static StorageValues ReservationValues(string owner) =>
            new(new Dictionary<string, object?>
            {
                ["identity"] = "provider|subject",
                ["owner"] = owner
            });

        static ReservationCommitResult CommitReservation(IUnitOfWork work, Barrier start)
        {
            start.SignalAndWait();
            try
            {
                return new ReservationCommitResult(work.CommitWithOutcomes(), null);
            }
            catch (Exception exception)
            {
                return new ReservationCommitResult(null, exception);
            }
        }
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Concurrent_transactional_multi_row_conflicts_stop_before_the_trailing_row(
        bool asynchronous,
        bool compareAndSwap)
    {
        var connectionString = LiveMongo.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB integration tests.");
        using var nativeSetup = new MongoDbProviderFactory().Create(connectionString!);
        Skip.If(nativeSetup.ProviderSequenceFit is ProviderFit.Unsupported,
            "MongoDB standalone deployments cannot execute unit-of-work transactions.");

        var name = "mongo_batch_conflict_" + Guid.NewGuid().ToString("N")[..20];
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns =
            [
                new() { Name = "identity", Type = PortableType.String, IsNullable = false, MaxLength = 128 },
                new() { Name = "owner", Type = PortableType.String, IsNullable = false, MaxLength = 128 }
            ],
            Key = new KeyDefinition { Columns = ["identity"] },
            Concurrency = ConcurrencyDeclaration.Optimistic()
        };
        Assert.True(nativeSetup.Schema.Apply(unit).Applied);

        if (compareAndSwap)
        {
            var seed = nativeSetup.OpenSession(unit, MongoStorageAccess.Global);
            var seeded = seed.Insert(MongoReservationValues("provider|subject", "seed"));
            Assert.Equal(MongoWriteOutcomeStatus.Inserted, seeded.Status);
        }

        using var firstConnection = new MongoProviderFactory().Create(connectionString!);
        using var secondConnection = new MongoProviderFactory().Create(connectionString!);
        using var first = firstConnection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit);
        using var second = secondConnection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit);
        var options = compareAndSwap ? WriteOptions.IfVersion(1) : WriteOptions.CreateOnly;
        first.Stage(RowWrite.ConditionalUpsert(unit, ReservationValues("provider|subject", "first"), options));
        first.Stage(RowWrite.ConditionalUpsert(unit, ReservationValues("trailing-first", "first"), WriteOptions.CreateOnly));
        second.Stage(RowWrite.ConditionalUpsert(unit, ReservationValues("provider|subject", "second"), options));
        second.Stage(RowWrite.ConditionalUpsert(unit, ReservationValues("trailing-second", "second"), WriteOptions.CreateOnly));

        using var start = new Barrier(2);
        var firstTask = Task.Run(() => CommitReservation(first, start, asynchronous));
        var secondTask = Task.Run(() => CommitReservation(second, start, asynchronous));
        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.Single(results, result => result.Report?.IsSuccessful == true);
        var loser = Assert.Single(results, result => result.Report is null);
        var batchError = Assert.IsType<BatchWriteException>(loser.Error);
        var failure = Assert.Single(batchError.Outcomes);
        Assert.Equal(WriteOutcomeStatus.ConcurrencyConflict, failure.Outcome.Status);
        Assert.Equal("provider|subject", failure.Write.Values!.Values["identity"]);

        var winner = firstConnection.OpenSession(unit, StorageAccess.Global).Read(new StorageKey(
            new Dictionary<string, object?> { ["identity"] = "provider|subject" }));
        Assert.NotNull(winner);
        var winnerOwner = Assert.IsType<string>(winner!.Values.Values["owner"]);
        Assert.True(compareAndSwap ? winnerOwner == "first" || winnerOwner == "second" : winnerOwner is "first" or "second");
        var winnerTrailing = winnerOwner == "first" ? "trailing-first" : "trailing-second";
        var loserTrailing = winnerOwner == "first" ? "trailing-second" : "trailing-first";
        Assert.NotNull(firstConnection.OpenSession(unit, StorageAccess.Global).Read(new StorageKey(
            new Dictionary<string, object?> { ["identity"] = winnerTrailing })));
        Assert.Null(firstConnection.OpenSession(unit, StorageAccess.Global).Read(new StorageKey(
            new Dictionary<string, object?> { ["identity"] = loserTrailing })));

        static StorageValues ReservationValues(string identity, string owner) =>
            new(new Dictionary<string, object?>
            {
                ["identity"] = identity,
                ["owner"] = owner
            });

        static MongoStorageValues MongoReservationValues(string identity, string owner) =>
            new(new Dictionary<string, object?>
            {
                ["identity"] = identity,
                ["owner"] = owner
            });

        static async Task<ReservationCommitResult> CommitReservation(
            IUnitOfWork work,
            Barrier start,
            bool asynchronous)
        {
            start.SignalAndWait();
            try
            {
                return asynchronous
                    ? new ReservationCommitResult(await work.CommitWithOutcomesAsync(), null)
                    : new ReservationCommitResult(work.CommitWithOutcomes(), null);
            }
            catch (Exception exception)
            {
                return new ReservationCommitResult(null, exception);
            }
        }
    }

    [SkippableFact]
    public void Transaction_body_retries_a_transient_write_conflict_before_returning_success()
    {
        var connectionString = LiveMongo.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB integration tests.");
        using var native = new MongoDbProviderFactory().Create(connectionString!);
        Skip.If(native.ProviderSequenceFit is ProviderFit.Unsupported,
            "MongoDB standalone deployments cannot execute unit-of-work transactions.");

        var name = "mongo_retry_body_" + Guid.NewGuid().ToString("N")[..20];
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns =
            [
                new() { Name = "sequence", Type = PortableType.Int64, IsNullable = false, Generation = ColumnGeneration.ProviderSequence },
                new() { Name = "payload", Type = PortableType.String, IsNullable = false, MaxLength = 128 }
            ],
            Key = new KeyDefinition { Columns = ["sequence"] }
        };
        Assert.True(native.Schema.Apply(unit).Applied);

        using var failpointClient = new MongoClient(connectionString!);
        var admin = failpointClient.GetDatabase("admin");
        var failpointEnabled = false;
        try
        {
            try
            {
                admin.RunCommand<BsonDocument>(new BsonDocument
                {
                    ["configureFailPoint"] = "failCommand",
                    ["mode"] = new BsonDocument("times", 1),
                    ["data"] = new BsonDocument
                    {
                        ["failCommands"] = new BsonArray { "insert" },
                        ["errorCode"] = 112,
                        ["errorLabels"] = new BsonArray { "TransientTransactionError" }
                    }
                });
                failpointEnabled = true;
            }
            catch (MongoCommandException exception)
            {
                Skip.If(true, $"MongoDB failCommand is unavailable: {exception.Message}");
            }

            using var connection = new MongoProviderFactory().Create(connectionString!);
            var outcome = connection.OpenSession(unit, StorageAccess.Global).Insert(
                new StorageValues(new Dictionary<string, object?> { ["payload"] = "retry-me" }));

            Assert.Equal(WriteOutcomeStatus.Inserted, outcome.Status);
            Assert.Equal(1L, outcome.GeneratedValue<long>("sequence"));
        }
        finally
        {
            if (failpointEnabled)
            {
                try
                {
                    admin.RunCommand<BsonDocument>(new BsonDocument
                    {
                        ["configureFailPoint"] = "failCommand",
                        ["mode"] = "off"
                    });
                }
                catch (MongoException)
                {
                    // Keep the original test failure if disabling the test-only failpoint fails.
                }
            }
        }
    }

    private sealed record ReservationCommitResult(BatchWriteReport? Report, Exception? Error);

    [SkippableFact]
    public void Provider_sequence_is_capability_gated_by_mongodb_transactions()
    {
        var connectionString = LiveMongo.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB integration tests.");
        using var connection = OpenConnection();
        using var store = new MongoProviderFactory().Create(connectionString!);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("p1-sequence-" + Guid.NewGuid().ToString("N")),
            Name = "P1Sequence_" + Guid.NewGuid().ToString("N"),
            Columns =
            [
                new() { Name = "sequence", Type = PortableType.Int64, IsNullable = false, Generation = ColumnGeneration.ProviderSequence },
                new() { Name = "payload", Type = PortableType.String }
            ],
            Key = new KeyDefinition { Columns = ["sequence"] }
        };

        var hello = Assert.IsType<MongoDbProviderConnection>(connection).Database
            .RunCommand<BsonDocument>(new BsonDocument("hello", 1));
        if (!hello.Contains("setName") && !string.Equals(hello.GetValue("msg", "").AsString, "isdbgrid", StringComparison.Ordinal))
        {
            var fit = Assert.IsType<ProviderFit.Unsupported>(connection.ProviderSequenceFit);
            Assert.Contains(MongoCapabilities.ProviderSequence, fit.MissingRequirements);
            Assert.DoesNotContain(store.Capabilities,
                capability => capability.Id == WellKnownCapabilities.AtomicCommit);
            var refusal = Assert.Throws<InvalidOperationException>(() => connection.Schema.Apply(unit));
            Assert.Contains("transaction-capable", refusal.Message, StringComparison.OrdinalIgnoreCase);

            var compareUnit = new StorageUnit
            {
                Id = new StorageUnitId("mongo-standalone-compare-" + Guid.NewGuid().ToString("N")),
                Name = "MongoStandaloneCompare_" + Guid.NewGuid().ToString("N"),
                Columns =
                [
                    new() { Name = "id", Type = PortableType.String, IsNullable = false },
                    new() { Name = "owner", Type = PortableType.String }
                ],
                Key = new KeyDefinition { Columns = ["id"] }
            };
            Assert.True(store.Schema.Apply(compareUnit).Applied);
            Assert.DoesNotContain(store.Capabilities,
                capability => capability.Id == BatchWriteCapabilities.CompareAndDelete);
            var observer = new ProviderCommandObserver();
            var compareSession = store.OpenSession(compareUnit, StorageAccess.Global, observer);
            Assert.False(compareSession is ICompareAndDeleteStorageSession);
            Assert.Throws<NotSupportedException>(() => compareSession.CompareAndDelete(
                new StorageKey(new Dictionary<string, object?> { ["id"] = "missing" }),
                new Dictionary<string, object?> { ["owner"] = "worker" }));
            Assert.Empty(observer.Commands);
            var uowRefusal = Assert.Throws<InvalidOperationException>(() =>
                store.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, compareUnit));
            Assert.Contains("transaction", uowRefusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(observer.Commands);
            return;
        }

        Assert.IsType<ProviderFit.Supported>(connection.ProviderSequenceFit);
        Assert.Contains(store.Capabilities,
            capability => capability.Id == WellKnownCapabilities.AtomicCommit);
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, MongoStorageAccess.Global);
        var result = session.Insert(new MongoStorageValues(new Dictionary<string, object?> { ["payload"] = "sequence" }));
        Assert.True(result.Succeeded);
        Assert.Equal(1L, result.GeneratedValue<long>("sequence"));
    }

    [SkippableFact]
    public void Scoped_unit_of_work_registration_is_visible_to_privileged_queries()
    {
        using var connection = OpenConnection();
        var unit = ScopedCrossScopeUnit("uow-registry");
        Assert.True(connection.Schema.Apply(unit).Applied);
        var scope = MongoStorageAccess.Scoped(new StorageScope("uow-only-\U00010000"));

        using (var unitOfWork = connection.BeginUnitOfWork(scope, unit))
        {
            var outcome = unitOfWork.OpenSession(unit).Insert(new MongoStorageValues(
                new Dictionary<string, object?> { ["id"] = "one", ["value"] = "from-uow" }));
            Assert.Equal(MongoWriteOutcomeStatus.Inserted, outcome.Status);
            unitOfWork.Commit();
        }

        var privileged = connection.OpenSession(unit, MongoStorageAccess.PrivilegedAcrossScopes(
            new StorageAccessAudit(
                "mongo-registry-test",
                "verify-uow-only-scope",
                new RecordingAccessObserver())));
        Assert.Throws<InvalidOperationException>(() => privileged.Read(
            new MongoStorageKey(new Dictionary<string, object?> { ["id"] = "one" })));
        Assert.Throws<InvalidOperationException>(() => privileged.Insert(new MongoStorageValues(
            new Dictionary<string, object?> { ["id"] = "refused", ["value"] = "refused" })));
        Assert.Throws<InvalidOperationException>(() => privileged.Aggregate(new AggregationQuery("refused")));
        var result = privileged.QueryAcrossScopes(new QueryRequest(
            new TableId(unit.Name),
            Predicate.AlwaysTrue.Instance,
            [],
            Projection.All,
            Paging.None));

        var row = Assert.Single(result.Rows);
        Assert.Equal("uow-only-\U00010000", row.Scope.Value);
        Assert.Equal("from-uow", row.Values["value"]);
    }

    [SkippableFact]
    public void Privileged_query_refuses_provider_scope_registry_drift()
    {
        using var connection = OpenConnection();
        var unit = ScopedCrossScopeUnit("registry-drift");
        Assert.True(connection.Schema.Apply(unit).Applied);
        connection.OpenSession(unit, MongoStorageAccess.Scoped(new StorageScope("tenant-a")))
            .Insert(new MongoStorageValues(
                new Dictionary<string, object?> { ["id"] = "one", ["value"] = "visible" }));
        var native = Assert.IsType<MongoDbProviderConnection>(connection);
        native.Database.GetCollection<BsonDocument>("__groundwork_metadata").UpdateOne(
            new BsonDocument { ["kind"] = "scope", ["unit"] = unit.Id.Value },
            new BsonDocument("$set", new BsonDocument("collection", "forged-collection")));
        var privileged = connection.OpenSession(unit, MongoStorageAccess.PrivilegedAcrossScopes(
            new StorageAccessAudit(
                "mongo-registry-test",
                "verify-registry-drift-refusal",
                new RecordingAccessObserver())));

        var failure = Assert.Throws<InvalidOperationException>(() => privileged.QueryAcrossScopes(
            new QueryRequest(new TableId(unit.Name), Predicate.AlwaysTrue.Instance, [], Projection.All, Paging.None)));

        Assert.Contains("GW-ACCESS-006", failure.Message, StringComparison.Ordinal);
    }

    private static void AssertPointReadCommand(
        ProviderCommandEvent command,
        string expectedCollection,
        IReadOnlyList<string> expectedIdentityFields,
        params string[] redactedValues)
    {
        Assert.Equal("mongodb.read", command.Operation);
        Assert.Equal(ProviderCommandKind.Read, command.Kind);
        Assert.False(command.IsProbe);
        Assert.NotNull(command.CommandText);

        using var document = JsonDocument.Parse(command.CommandText);
        var root = document.RootElement;
        Assert.Equal(
            ["collection", "filter", "limit"],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(expectedCollection, root.GetProperty("collection").GetString());
        Assert.Equal(1, root.GetProperty("limit").GetInt32());
        Assert.False(root.TryGetProperty("sort", out _));

        var filter = root.GetProperty("filter");
        Assert.Equal(["_id"], filter.EnumerateObject().Select(property => property.Name));
        var equality = filter.GetProperty("_id");
        Assert.Equal(["$eq"], equality.EnumerateObject().Select(property => property.Name));
        var redactedIdentity = equality.GetProperty("$eq");
        if (expectedIdentityFields.Count == 0)
            Assert.Equal("<redacted>", redactedIdentity.GetString());
        else
        {
            Assert.Equal(expectedIdentityFields, redactedIdentity.EnumerateObject().Select(property => property.Name));
            Assert.All(redactedIdentity.EnumerateObject(), property =>
                Assert.Equal("<redacted>", property.Value.GetString()));
        }
        Assert.All(redactedValues, value =>
            Assert.DoesNotContain(value, command.CommandText, StringComparison.Ordinal));
    }

    private static ProviderCommandEvent ObservePointRead(
        IMongoProviderConnection connection,
        StorageUnit unit,
        MongoStorageAccess access,
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyDictionary<string, object?> key)
    {
        Assert.True(connection.Schema.Apply(unit).Applied);
        var seeded = connection.OpenSession(unit, access);
        Assert.True(seeded.Insert(new MongoStorageValues(values)).Succeeded);

        var observer = new ProviderCommandObserver();
        var session = connection.OpenSession(unit, access, observer);
        Assert.NotNull(session.Read(new MongoStorageKey(key)));
        return Assert.Single(observer.Commands);
    }

    private static StorageUnit PointReadUnit(string idPrefix, ScopePolicy scope)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new StorageUnit
        {
            Id = new StorageUnitId($"mongo-{idPrefix}-{suffix}"),
            Name = $"mongo_{idPrefix}_{suffix}",
            Scope = scope,
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
                new() { Name = "payload", Type = PortableType.String, MaxLength = 64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
    }

    private static StorageUnit CompositePointReadUnit()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new StorageUnit
        {
            Id = new StorageUnitId($"mongo-composite-observer-{suffix}"),
            Name = $"mongo_composite_observer_{suffix}",
            Scope = ScopePolicy.Global,
            Columns =
            [
                new() { Name = "tenantKey", Type = PortableType.String, MaxLength = 64, IsNullable = false },
                new() { Name = "recordKey", Type = PortableType.String, MaxLength = 64, IsNullable = false },
                new() { Name = "payload", Type = PortableType.String, MaxLength = 64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["tenantKey", "recordKey"] }
        };
    }

    private static StorageUnit ScopedCrossScopeUnit(string prefix)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new StorageUnit
        {
            Id = new StorageUnitId($"mongo-{prefix}-{suffix}"),
            Name = $"mongo_{prefix.Replace("-", "_", StringComparison.Ordinal)}_{suffix}",
            Scope = ScopePolicy.Scoped,
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false, MaxLength = 64 },
                new() { Name = "value", Type = PortableType.String, IsNullable = false, MaxLength = 64 }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
    }

    private static MongoStorageValues CustomerValues(string id, string? email) => new(new Dictionary<string, object?>
    {
        ["id"] = id,
        ["name"] = "Ada",
        ["email"] = email,
        ["createdAt"] = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero),
        ["isActive"] = true,
        ["balance"] = 1.23456m
    });

    private static MongoStorageKey Key(string id) => new(new Dictionary<string, object?> { ["id"] = id });

    private static StorageUnit RequiredFoldedUnit(
        string idPrefix,
        bool uniqueStatus = false,
        ConcurrencyDeclaration? concurrency = null)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new StorageUnit
        {
            Id = new StorageUnitId(idPrefix + "-" + suffix),
            Name = "Q9MongoRequiredFolded_" + suffix,
            Columns =
            [
                new() { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new() { Name = "status", Type = PortableType.String, MaxLength = 32, IsNullable = false, Collation = PortableCollation.OrdinalIgnoreCase }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Indexes =
            [
                new IndexDefinition
                {
                    Name = uniqueStatus ? "unique_status" : "by_status",
                    Columns = [new IndexColumn("status")],
                    IsUnique = uniqueStatus
                }
            ],
            Concurrency = concurrency ?? ConcurrencyDeclaration.None
        };
    }

    private static StorageUnit OptimisticUpsertUnit(string idPrefix, ScopePolicy scope = ScopePolicy.Global)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new StorageUnit
        {
            Id = new StorageUnitId($"mongo-{idPrefix}-{suffix}"),
            Name = $"MongoOptimisticUpsert_{suffix}",
            Scope = scope,
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false, MaxLength = 64 },
                new() { Name = "payload", Type = PortableType.String, IsNullable = false, MaxLength = 64 }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Concurrency = ConcurrencyDeclaration.Optimistic()
        };
    }

    private static MongoStorageValues OptimisticUpsertValues(string id, string payload) =>
        new(new Dictionary<string, object?> { ["id"] = id, ["payload"] = payload });

    private static StorageValues OptimisticUpsertStoreValues(string id, string payload) =>
        new(new Dictionary<string, object?> { ["id"] = id, ["payload"] = payload });

    private static MongoJoinFixture JoinFixture(string idPrefix, ScopePolicy scope)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var target = new StorageUnit
        {
            Id = new StorageUnitId($"mongo-{idPrefix}-target-{suffix}"),
            Name = $"MongoJoinTarget_{suffix}",
            Scope = scope,
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false, MaxLength = 64 },
                new() { Name = "score", Type = PortableType.Int32, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Indexes =
            [
                new IndexDefinition
                {
                    Name = "by_id_score",
                    Columns = [new IndexColumn("id"), new IndexColumn("score")]
                }
            ]
        };
        var source = new StorageUnit
        {
            Id = new StorageUnitId($"mongo-{idPrefix}-source-{suffix}"),
            Name = $"MongoJoinSource_{suffix}",
            Scope = scope,
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false, MaxLength = 64 },
                new() { Name = "customer_id", Type = PortableType.String, IsNullable = false, MaxLength = 64 }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Indexes = [new IndexDefinition { Name = "by_customer", Columns = [new IndexColumn("customer_id")] }],
            References =
            [
                new ReferenceDefinition
                {
                    Name = "customer",
                    Columns = ["customer_id"],
                    TargetUnitId = target.Id,
                    TargetScope = scope
                }
            ]
        };
        var sourceTable = new TableId(source.Name);
        var targetTable = new TableId(target.Name);
        var targetScore = new ColumnRef(targetTable, "score", QueryType.Int32, isNullable: false);
        return new MongoJoinFixture(
            source,
            target,
            sourceTable,
            new ReferenceJoin(
                "customer",
                targetTable,
                [new JoinColumnPair(
                    new ColumnRef(sourceTable, "customer_id", QueryType.String, isNullable: false),
                    new ColumnRef(targetTable, "id", QueryType.String, isNullable: false))]),
            targetScore);
    }

    private sealed record MongoJoinFixture(
        StorageUnit Source,
        StorageUnit Target,
        TableId SourceTable,
        ReferenceJoin Join,
        ColumnRef TargetScore);

    [SkippableFact]
    public void Provider_side_count_over_an_empty_collection_reports_zero()
    {
        using var connection = OpenConnection();
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("mongo.empty.count"),
            Name = "mongo_empty_count",
            Columns = [new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false }],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, MongoStorageAccess.Global);

        var counted = session.Query(QueryRequestExecution.ForProviderCount(new QueryRequest(
            new TableId(unit.Name), Predicate.AlwaysTrue.Instance, [], Projection.All, Paging.None)));

        Assert.Equal(0L, counted.TotalCount);
    }

    private static StorageUnit RuntimeAdmissionUnit() => new()
    {
        Id = new StorageUnitId("issue201-mongo-" + Guid.NewGuid().ToString("N")),
        Name = "issue201_mongo_" + Guid.NewGuid().ToString("N"),
        Columns =
        [
            new() { Name = "id", Type = PortableType.Int32, IsNullable = false },
            new() { Name = "payload", Type = PortableType.String, MaxLength = 64, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes = [new IndexDefinition { Name = "by_payload", Columns = [new IndexColumn("payload")] }]
    };

    private static StorageUnit JsonAdmissionUnit(string name, bool nullable) => new()
    {
        Id = new StorageUnitId(name),
        Name = name.Replace('-', '_'),
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, IsNullable = false },
            new() { Name = "payload", Type = PortableType.Json, IsNullable = nullable }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static IMongoProviderConnection OpenConnection()
    {
        var connectionString = LiveMongo.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB integration tests.");
        return new MongoDbProviderFactory().Create(connectionString!);
    }

    private sealed class RecordingAccessObserver : IStorageAccessObserver
    {
        public List<StorageAccessEvent> Events { get; } = [];

        public void Observe(StorageAccessEvent accessEvent) => Events.Add(accessEvent);
    }

}
