using Groundwork.Kernel;
using Groundwork.MongoDb;
using Groundwork.MongoDb.TestingAdapter;
using Groundwork.Query.Model;
using Groundwork.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Groundwork.MongoDb.Tests;

public sealed class MongoProviderIntegrationTests
{
    [SkippableFact]
    public void Provider_passes_the_shipped_conformance_suite()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB integration tests.");
        var url = new MongoUrlBuilder(connectionString) { DatabaseName = "p1conformance_" + Guid.NewGuid().ToString("N") };

        var report = ConformanceSuite.Run(new MongoDbTestingFactory(), url.ToMongoUrl().ToString());

        Assert.True(report.Passed, string.Join(Environment.NewLine,
            report.Failures.Select(failure => $"{failure.Name}: {failure.Failure}")));
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
            Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION")!);
        var reopenedIndexes = reopened.Catalog.ReadIndexes(unit.Id);
        var native = Assert.IsType<MongoDbProviderConnection>(connection).Database
            .GetCollection<BsonDocument>(unit.Name);
        var indexNames = native.Indexes.List().ToList().Select(index => index["name"].AsString).ToArray();

        Assert.False(first.IsNoOp);
        Assert.True(second.IsNoOp);
        Assert.Contains(indexes, index => index.Name == "unique-email" &&
            index.IsUnique && index.MissingValues == MissingValueBehavior.Excluded);
        Assert.Contains(reopenedIndexes, index => index.Name == "unique-email");
        Assert.Contains("unique-email", indexNames);

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

    [SkippableFact]
    public void Composite_key_reordering_is_refused_after_reopening_the_provider()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
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
        var refusal = Assert.Throws<InvalidOperationException>(() => reopened.Schema.Apply(reordered));

        Assert.Contains("GW-PORT-008", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("native _id field order", refusal.Message, StringComparison.Ordinal);
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
        native.Indexes.DropOne("unique-email");

        var report = connection.InspectSchema(unit, MongoStorageAccess.Global);

        Assert.True(report.IsProcessReady);
        Assert.Contains(report.IndexDrift, refusal => refusal.Code == "GW-RUNTIME-002" &&
            refusal.Path == "indexes.unique-email");
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
            Indexes = [new IndexDefinition { Name = "unique-status", Columns = [new IndexColumn("status")], IsUnique = true }]
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
            Indexes = [new IndexDefinition { Name = "unique-status", Columns = [new IndexColumn("status")], IsUnique = true }]
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
    public void Folded_algorithm_id_drift_is_refused_before_mongo_session_open()
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
        metadata.UpdateOne(
            new BsonDocument("_id", "schema:" + unit.Id.Value),
            new BsonDocument("$set", new BsonDocument("derived.0.algorithmId", "stale-search-key-v0")));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            connection.OpenSession(unit, MongoStorageAccess.Global));
        Assert.Contains("rebuild", exception.Message, StringComparison.OrdinalIgnoreCase);
        var report = connection.InspectSchema(unit, MongoStorageAccess.Global);
        Assert.Contains(report.ColumnDrift, refusal => refusal.Path.EndsWith("searchKeyAlgorithm", StringComparison.Ordinal));
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

        var batch = Assert.IsAssignableFrom<IBatchedStorageSession>(session);
        var aggregateObserver = new WritePathObserver();
        var aggregate = batch.ApplyBatch(
            [RowWrite.Upsert(unit, new StorageValues(new Dictionary<string, object?> { ["id"] = 1 }),
                new WriteOptions { Observer = aggregateObserver })]);
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
    public void Folded_prefix_uses_the_optimizer_selected_physical_index_without_a_hint()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
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
                Indexes = [new IndexDefinition { Name = "by-status", Columns = [new IndexColumn("status")] }]
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
                [new QueryIndexDeclaration("by-status", [new QueryIndexColumn("status", false, QueryType.String)], QueryIndexPinning.ProviderDefault)],
                selectedIndex: "by-status");
            var result = session.Query(new QueryRequest(table,
                new Predicate.StartsWith(status, "OP"), [], Projection.All, Paging.None), options);

            Assert.Equal("by-status", result.SelectedIndex);
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

    [SkippableFact]
    public void Provider_sequence_is_capability_gated_by_mongodb_transactions()
    {
        using var connection = OpenConnection();
        var unit = TestUnits.Customer with
        {
            Id = new StorageUnitId("p1-sequence-" + Guid.NewGuid().ToString("N")),
            Name = "P1Sequence_" + Guid.NewGuid().ToString("N"),
            Columns = [.. TestUnits.Customer.Columns, new ColumnDefinition
            {
                Name = "sequence", Type = PortableType.Int64, IsNullable = false,
                Generation = ColumnGeneration.ProviderSequence
            }]
        };

        var hello = Assert.IsType<MongoDbProviderConnection>(connection).Database
            .RunCommand<BsonDocument>(new BsonDocument("hello", 1));
        if (!hello.Contains("setName") && !string.Equals(hello.GetValue("msg", "").AsString, "isdbgrid", StringComparison.Ordinal))
        {
            var refusal = Assert.Throws<InvalidOperationException>(() => connection.Schema.Apply(unit));
            Assert.Contains("transaction-capable", refusal.Message, StringComparison.OrdinalIgnoreCase);
            return;
        }

        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, MongoStorageAccess.Global);
        var result = session.Insert(CustomerValues("sequence", null));
        Assert.True(result.Succeeded);
        Assert.Equal(1L, session.Read(Key("sequence"))!.Values.Values["sequence"]);
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
                    Name = uniqueStatus ? "unique-status" : "by-status",
                    Columns = [new IndexColumn("status")],
                    IsUnique = uniqueStatus
                }
            ],
            Concurrency = concurrency ?? ConcurrencyDeclaration.None
        };
    }

    private static IMongoProviderConnection OpenConnection()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB integration tests.");
        return new MongoDbProviderFactory().Create(connectionString!);
    }

}
