using Groundwork.Kernel;
using Groundwork.MongoDb;
using Groundwork.Query.Model;
using Groundwork.Testing;
using Groundwork.Store;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Groundwork.MongoDb.Tests;

public sealed class MongoProviderIntegrationTests
{
    [SkippableFact]
    public void A_63_byte_storage_unit_name_applies_without_provider_rewriting()
    {
        using var connection = OpenConnection();
        var name = new string('a', PortabilityValidator.MaximumPortableIdentifierLength);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("logical.boundary.id"),
            Name = name,
            Columns = [new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false }],
            Key = new KeyDefinition { Columns = ["id"] }
        };

        Assert.True(connection.Schema.Apply(unit).Applied);
        Assert.True(connection.Schema.Diff(unit).IsEmpty);
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
    public void Profile_only_reducer_alias_and_budget_changes_are_reported_and_applied()
    {
        using var connection = OpenConnection();
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("mongo-aggregation-profile-drift-" + Guid.NewGuid().ToString("N")),
            Name = "mongo_aggregation_profile_drift_" + Guid.NewGuid().ToString("N"),
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
            Assert.Contains(diff.Changes, change =>
                change.Kind == MongoSchemaChangeKind.UpdateAggregationProfile && change.Identity == "summary");
            Assert.True(connection.Schema.Apply(changed).Applied);
        }

        Assert.False(connection.Schema.Apply(budgetChanged).Applied);
    }

    [SkippableFact]
    public void Provider_passes_the_shipped_conformance_suite()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB integration tests.");
        var url = new MongoUrlBuilder(connectionString) { DatabaseName = "p1conformance_" + Guid.NewGuid().ToString("N") };

        var report = ConformanceSuite.Run(new MongoProviderFactory(), url.ToMongoUrl().ToString());

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
    public void Provider_sequence_is_capability_gated_by_mongodb_transactions()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
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
            new StorageAccessAudit("mongo-registry-test", "verify-uow-only-scope")));
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
            new StorageAccessAudit("mongo-registry-test", "verify-registry-drift-refusal")));

        var failure = Assert.Throws<InvalidOperationException>(() => privileged.QueryAcrossScopes(
            new QueryRequest(new TableId(unit.Name), Predicate.AlwaysTrue.Instance, [], Projection.All, Paging.None)));

        Assert.Contains("GW-ACCESS-006", failure.Message, StringComparison.Ordinal);
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

    private static IMongoProviderConnection OpenConnection()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB integration tests.");
        return new MongoDbProviderFactory().Create(connectionString!);
    }

}
