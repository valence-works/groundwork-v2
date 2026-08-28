using Groundwork.Documents;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Query.Linq;
using Groundwork.Query.Planning;
using Groundwork.Records;
using Groundwork.Store;
using Groundwork.Sqlite;
using Groundwork.Testing;
using KernelStorageUnit = Groundwork.Kernel.StorageUnit;

using Groundwork.PublicApi.Consumer;

PublicApiApprovalFixture.Touch();
PublicApiApprovalFixture.CompileCallableSurface();

using (var externalProvider = new InMemoryProviderFactory().Create("external-provider-author-proof"))
{
    Require(externalProvider.Capabilities.Any(capability => capability.Id == BatchWriteCapabilities.ExactAppendOutcomes), "Groundwork.Testing did not expose the reference provider capability contract.");
}

var databasePath = Path.Combine(Path.GetTempPath(), "groundwork-public-api-" + Guid.NewGuid().ToString("N") + ".db");
try
{
    using var connection = new SqliteProviderFactory().Create("Data Source=" + databasePath);
    Require(connection.Capabilities.Any(capability => capability.Id == WellKnownCapabilities.AtomicCommit),
        "The package-only SQLite provider did not advertise cross-unit atomic commit.");
    RunRecordsJourney(connection);
    RunPrivilegedCrossScopeJourney(connection);
    RunExactAppendJourney(connection);
    RunCompareAndDeleteJourney(connection);
    RunSetMutationJourney(connection);
    RunLifecycleJourney(connection);
    RunAggregationSourcePredicateJourney(connection);
    RunTimeBucketJourney(connection);
    RunDocumentsJourney(connection);
    RunFailureJourneys(connection);
    Console.WriteLine("Groundwork public API clean-room journey passed.");
}
finally
{
    if (File.Exists(databasePath))
        File.Delete(databasePath);
}

static void RunPrivilegedCrossScopeJourney(IStorageProviderConnection connection)
{
    var unit = new KernelStorageUnit
    {
        Id = new StorageUnitId("cross_scope_records"),
        Name = "cross_scope_records",
        Scope = ScopePolicy.Scoped,
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
            new() { Name = "value", Type = PortableType.String, MaxLength = 64, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };
    Require(connection.Schema.Apply(unit).Applied,
        "The package-only cross-scope schema did not apply.");
    foreach (var scope in new[] { "tenant-a", "tenant-b" })
    {
        var session = connection.OpenSession(unit,
            StorageAccess.Scoped(new StorageScope(scope)));
        Require(session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "same",
            ["value"] = scope
        })).Status == WriteOutcomeStatus.Inserted,
            "The package-only scoped source row did not insert.");
    }

    var observer = new PublicAccessObserver();
    var privileged = connection.OpenSession(unit, StorageAccess.PrivilegedAcrossScopes(
        new StorageAccessAudit(
            "public-api-consumer",
            "recover-stalled-workflows",
            observer)));
    var result = privileged.QueryAcrossScopes(new QueryRequest(
        new TableId(unit.Name),
        Predicate.AlwaysTrue.Instance,
        [],
        Projection.All,
        Paging.Keyset(1),
        ResultShape.TotalCount.Instance));

    Require(result.TotalCount == 2 && result.Rows.Count == 1 &&
            result.NextContinuationToken is not null,
        "The package-only privileged query did not preserve counted paging.");
    Require(result.Rows[0].Values.ContainsKey("id") &&
            !result.Rows[0].Values.Keys.Any(key => key.StartsWith("__groundwork_", StringComparison.Ordinal)),
        "The package-only privileged query leaked provider-owned columns.");
    Require(observer.Events.Count == 1 &&
            observer.Events[0].Purpose == "recover-stalled-workflows",
        "The package-only privileged query did not emit audit evidence.");
    try
    {
        privileged.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "same" }));
        throw new InvalidOperationException("Privileged access accepted an ambiguous point read.");
    }
    catch (InvalidOperationException exception)
    {
        Require(exception.Message.Contains("GW-ACCESS-003", StringComparison.Ordinal),
            "The privileged point-read refusal did not expose its stable access code.");
    }
}

static void RunExactAppendJourney(IStorageProviderConnection connection)
{
    var unit = new KernelStorageUnit
    {
        Id = new StorageUnitId("append_records"),
        Name = "append_records",
        Columns =
        [
            new() { Name = "sequence", Type = PortableType.Int64, IsNullable = false, Generation = ColumnGeneration.ProviderSequence },
            new() { Name = "payload", Type = PortableType.String, IsNullable = false, MaxLength = 200 }
        ],
        Key = new KeyDefinition { Columns = ["sequence"] },
        AppendIdempotency = new AppendIdempotencyDeclaration { Window = TimeSpan.FromMinutes(10) }
    };
    Require(connection.Schema.Apply(unit).Applied, "The exact append schema did not apply.");

    var session = connection.OpenSession(unit, StorageAccess.Global);
    var operation = new OperationId(DateTimeOffset.UtcNow, "public-exact-append");
    var values = new StorageValues(new Dictionary<string, object?> { ["payload"] = "package-only" });
    var committed = session.AppendWithOutcomes(operation, values);
    Require(committed.Status == WriteOutcomeStatus.Inserted && committed.Outcomes.Count == 1, "The package-only exact append did not return one inserted outcome.");
    Require(committed.Outcomes[0].GeneratedValue<long>("sequence") == 1, "The package-only exact append did not return the generated sequence.");

    var replayed = session.AppendWithOutcomes(operation, values);
    Require(replayed.Status == WriteOutcomeStatus.Replayed, "The package-only exact append did not replay.");
    Require(replayed.Outcomes[0].GeneratedValue<long>("sequence") == 1, "The package-only replay did not preserve its generated sequence.");
}

static void RunCompareAndDeleteJourney(IStorageProviderConnection connection)
{
    var unit = new KernelStorageUnit
    {
        Id = new StorageUnitId("compare_delete_records"),
        Name = "compare_delete_records",
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
            new() { Name = "owner", Type = PortableType.String, MaxLength = 64 },
            new() { Name = "fence", Type = PortableType.Int64, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Concurrency = ConcurrencyDeclaration.Optimistic()
    };
    Require(connection.Schema.Apply(unit).Applied, "The package-only compare-and-delete schema did not apply.");
    Require(connection.Capabilities.Any(capability => capability.Id == BatchWriteCapabilities.CompareAndDelete),
        "The package-only provider did not advertise atomic compare-and-delete.");

    var session = connection.OpenSession(unit, StorageAccess.Global);
    Require(session is ICompareAndDeleteStorageSession,
        "The package-only session did not expose the advertised compare-and-delete capability.");
    var inserted = session.Insert(new StorageValues(new Dictionary<string, object?>
    {
        ["id"] = "claim", ["owner"] = "worker-a", ["fence"] = 7L
    }));
    Require(inserted.Status == WriteOutcomeStatus.Inserted && inserted.Version == 1,
        "The package-only compare-and-delete setup did not insert version 1.");

    var mismatch = session.CompareAndDelete(
        new StorageKey(new Dictionary<string, object?> { ["id"] = "claim" }),
        new Dictionary<string, object?> { ["owner"] = "worker-b", ["fence"] = 7L });
    Require(mismatch.Status == WriteOutcomeStatus.ComparisonMismatch && session.Read(
        new StorageKey(new Dictionary<string, object?> { ["id"] = "claim" })) is not null,
        "The package-only compare-and-delete did not preserve a mismatched claim.");

    var renewed = session.Update(new StorageValues(new Dictionary<string, object?>
    {
        ["id"] = "claim", ["owner"] = "worker-a", ["fence"] = 7L
    }), WriteOptions.IfVersion(1));
    Require(renewed.Status == WriteOutcomeStatus.Updated && renewed.Version == 2,
        "The package-only claim renewal did not advance its revision.");
    var deleted = session.CompareAndDelete(
        new StorageKey(new Dictionary<string, object?> { ["id"] = "claim" }),
        new Dictionary<string, object?> { ["owner"] = "worker-a", ["fence"] = 7L });
    Require(deleted.Status == WriteOutcomeStatus.Deleted,
        "The package-only compare-and-delete did not delete the renewed claim without a stale CAS token.");
    Require(session.CompareAndDelete(
        new StorageKey(new Dictionary<string, object?> { ["id"] = "claim" }),
        new Dictionary<string, object?> { ["owner"] = "worker-a" }).Status == WriteOutcomeStatus.NotFound,
        "The package-only compare-and-delete did not distinguish an absent claim.");

    session.Insert(new StorageValues(new Dictionary<string, object?>
    {
        ["id"] = "claim-exact", ["owner"] = "worker-a", ["fence"] = 9L
    }));
    var staged = RowWrite.CompareAndDelete(unit,
        new StorageKey(new Dictionary<string, object?> { ["id"] = "claim-exact" }),
        new Dictionary<string, object?> { ["owner"] = "worker-a", ["fence"] = 9L });
    using var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit);
    work.Stage(staged);
    var report = work.CommitWithOutcomes();
    var stagedOutcome = AssertSingle(report.Outcomes);
    Require(ReferenceEquals(stagedOutcome.Write, staged) &&
            stagedOutcome.Outcome.Status == WriteOutcomeStatus.Deleted,
        "The package-only exact batch did not attribute the staged compare-and-delete outcome.");
}

static void RunSetMutationJourney(IStorageProviderConnection connection)
{
    var unit = new KernelStorageUnit
    {
        Id = new StorageUnitId("set_mutation_records"),
        Name = "set_mutation_records",
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
            new() { Name = "status", Type = PortableType.String, MaxLength = 32, IsNullable = false },
            new() { Name = "value", Type = PortableType.String, MaxLength = 64, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes = [new IndexDefinition { Name = "by_status", Columns = [new IndexColumn("status")] }]
    };
    Require(connection.Schema.Apply(unit).Applied, "The package-only set-mutation schema did not apply.");
    var session = connection.OpenSession(unit, StorageAccess.Global);
    foreach (var id in new[] { "a", "b" })
    {
        Require(session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = id, ["status"] = "open", ["value"] = "before"
        })).Status == WriteOutcomeStatus.Inserted,
            "The package-only set-mutation setup row did not insert.");
    }

    var status = new ColumnRef(new TableId(unit.Name), "status", QueryType.String, isNullable: false, maxLength: 32);
    var result = session.UpdateWhere(
        new Predicate.Equal(status, QueryConstant.Of(status, "open")),
        new Dictionary<string, object?> { ["value"] = "after" },
        SetMutationOptions.Exact);
    Require(result.IsExact && result.MatchedRows == 2 && result.Outcomes.Count == 2,
        "The package-only exact set mutation did not return one outcome per selected row.");
    Require(result.Outcomes.All(item => item.Outcome.Status == WriteOutcomeStatus.Updated),
        "The package-only exact set mutation did not preserve keyed write statuses.");
}

static T AssertSingle<T>(IReadOnlyList<T> items)
{
    Require(items.Count == 1, "The package-only exact batch returned an unexpected outcome count.");
    return items[0];
}

static void RunLifecycleJourney(IStorageProviderConnection connection)
{
    var unit = new KernelStorageUnit
    {
        Id = new StorageUnitId("lifecycle_records"),
        Name = "lifecycle_records",
        Columns =
        [
            new() { Name = "sequence", Type = PortableType.Int64, IsNullable = false, Generation = ColumnGeneration.ProviderSequence },
            new() { Name = "payload", Type = PortableType.String, IsNullable = false, MaxLength = 200 }
        ],
        Key = new KeyDefinition { Columns = ["sequence"] },
        Retention = new RetentionDeclaration { KeepNewest = 1, OrderColumn = "sequence" },
        RetentionIdempotency = new RetentionIdempotencyDeclaration { Window = TimeSpan.FromMinutes(10) }
    };
    Require(connection.Schema.Apply(unit).Applied, "The lifecycle schema did not apply.");
    var session = connection.OpenSession(unit, StorageAccess.Global);
    session.Insert(new StorageValues(new Dictionary<string, object?> { ["payload"] = "first" }));
    session.Insert(new StorageValues(new Dictionary<string, object?> { ["payload"] = "second" }));
    Require(session.Inspect().LifetimeCommittedSequenceHighWater == 2, "The public lifecycle inspection did not expose the committed high-water.");
    var operation = new OperationId(DateTimeOffset.UtcNow, "public-retention");
    var executed = session.ApplyRetention(operation, new RetentionExecutionOptions { MaxRowsPerBatch = 1, KeepNewestOverride = 0 });
    var replayed = session.ApplyRetention(operation, new RetentionExecutionOptions { MaxRowsPerBatch = 1, KeepNewestOverride = 0 });
    Require(executed.Status == RetentionOperationStatus.Executed && executed.DeletedRows == 2, "The public exact retention override did not delete all rows.");
    Require(replayed.Status == RetentionOperationStatus.Replayed && replayed.DeletedRows == executed.DeletedRows, "The public exact retention did not replay its result.");
    Require(session.Inspect().LifetimeCommittedSequenceHighWater == 2, "The public exact retention override reset the sequence high-water.");
}

static void RunRecordsJourney(IStorageProviderConnection connection)
{
    var table = RecordTable.For<Customer>("customers")
        .Key(customer => customer.Id)
        .OptimisticConcurrency()
        .Column(customer => customer.Email, column => column.MaxLength(320).Required())
        .Column(customer => customer.Name, column => column.MaxLength(200).Required())
        .Index("by_email", customer => customer.Email)
        .Build();

    var applied = connection.Schema.Apply(table.Definition);
    Require(applied.Applied, "The initial public schema application did not apply.");
    Require(connection.Schema.Diff(table.Definition).IsEmpty, "The applied public schema was not a no-op on verification.");

    var records = table.Open(connection);
    var first = new Customer(Guid.NewGuid(), "ada@example.test", "Ada");
    var inserted = records.Insert(first);
    Require(inserted.Status == RecordWriteStatus.Inserted && inserted.Version == 1, "The typed insert did not return version 1.");

    var changed = first with { Name = "Ada Lovelace" };
    var updated = records.Update(changed, RecordWriteOptions.IfVersion(inserted.Version!.Value));
    Require(updated.Status == RecordWriteStatus.Updated && updated.Version == 2, "The exact conditional update did not advance version 1 to version 2.");

    var upserted = records.Upsert(changed with { Name = "Ada Byron" }, RecordWriteOptions.IfVersion(updated.Version!.Value));
    Require(upserted.Status is RecordWriteStatus.Updated or RecordWriteStatus.Upserted && upserted.Version == 3, "The exact conditional upsert did not advance version 2 to version 3.");

    var conflict = records.Upsert(changed with { Name = "stale" }, RecordWriteOptions.IfVersion(inserted.Version.Value));
    Require(conflict.Status == RecordWriteStatus.ConcurrencyConflict, "The stale conditional upsert did not report a concurrency conflict.");

    using (var batch = table.BeginUnitOfWork(connection, BatchWriteOptions.Exact))
    {
        batch.Upsert(new Customer(Guid.NewGuid(), "grace@example.test", "Grace"));
        batch.Upsert(new Customer(Guid.NewGuid(), "mary@example.test", "Mary"));
        var report = batch.CommitWithOutcomes();
        Require(report.Summary.IsSuccessful && report.Outcomes.Count == 2, "The exact typed batch did not return two successful outcomes.");
    }

    var query = table.Query.Where(customer => customer.Email == "ada@example.test");
    var matches = records.Query(query, RecordQueryOptions.UsingIndex("by_email"));
    Require(matches.Count == 1 && matches[0].Name == "Ada Byron", "The covered typed query did not return the updated customer.");

    var uncovered = new RuntimeCoverageGate(
        [new CoverageIndex("by_email", [new CoverageIndexColumn("email")])],
        []);
    try
    {
        uncovered.EnsureCovered(query.ToQueryRequest(), DateTimeOffset.UtcNow);
        throw new InvalidOperationException("The uncovered query was accepted without a deployed index.");
    }
    catch (QueryCoverageException exception)
    {
        Require(exception.Code == "GW-COVER-006", "The coverage refusal did not identify the missing index coverage code.");
        Require(exception.Message.Contains("index", StringComparison.OrdinalIgnoreCase), "The coverage refusal did not explain the corrective index action.");
    }
}

static void RunDocumentsJourney(IStorageProviderConnection connection)
{
    var unit = DocumentUnit.For<Note>("note", "notes")
        .Id(note => note.Id)
        .Project(note => note.CustomerId)
        .OptimisticConcurrency()
        .Build();
    Require(connection.Schema.Apply(unit.StorageUnit).Applied, "The Documents schema did not apply.");

    var note = new Note(Guid.NewGuid(), "ada@example.test", "Welcome");
    var write = unit.Insert(note, WriteOptions.CreateOnly);
    var outcome = unit.Execute(connection, write);
    Require(outcome.Status == WriteOutcomeStatus.Inserted, "The Documents insert did not use the ordinary Store write path.");

    var persisted = connection.OpenSession(unit.StorageUnit, StorageAccess.Global).Read(new StorageKey(
        new Dictionary<string, object?> { [unit.IdColumn] = note.Id }));
    Require(persisted is not null, "The inserted document could not be read through the public Store session.");
    var materialized = unit.Read(new RowValues(persisted!.Values.Values), persisted.Version);
    Require(materialized.Value == note && materialized.Version == outcome.Version, "The Documents read did not preserve the typed value and version.");
}

static void RunAggregationSourcePredicateJourney(IStorageProviderConnection connection)
{
    var unit = new KernelStorageUnit
    {
        Id = new StorageUnitId("aggregation_source_predicate"),
        Name = "aggregation_source_predicate",
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, MaxLength = 32, IsNullable = false },
            new() { Name = "group", Type = PortableType.String, MaxLength = 32, IsNullable = false },
            new() { Name = "amount", Type = PortableType.Int32 },
            new() { Name = "lowOrder", Type = PortableType.Int64, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        AggregationProfiles =
        [
            new AggregationProfile
            {
                Name = "summary",
                GroupByColumns = ["group"],
                Aggregates = [new Aggregate.Sum("total", "amount")],
                AllowedPredicates =
                [
                    new AggregationPredicateAllowance
                    {
                        Alias = "total",
                        SupportedPredicates = new HashSet<AggregationPredicateOperator>
                        {
                            AggregationPredicateOperator.Equal
                        }
                    }
                ]
            }
        ]
    };
    Require(connection.Schema.Apply(unit).Applied, "The package-only aggregation schema did not apply.");
    var session = connection.OpenSession(unit, StorageAccess.Global);
    Require(session.Insert(new StorageValues(new Dictionary<string, object?>
    {
        ["id"] = "1", ["group"] = "a", ["amount"] = 7, ["lowOrder"] = 2L
    })).Status == WriteOutcomeStatus.Inserted, "The package-only aggregation source row did not insert.");
    Require(session.Insert(new StorageValues(new Dictionary<string, object?>
    {
        ["id"] = "2", ["group"] = "a", ["amount"] = 11, ["lowOrder"] = 1L
    })).Status == WriteOutcomeStatus.Inserted, "The package-only aggregation post row did not insert.");

    var lowOrder = new ColumnRef(new TableId(unit.Name), "lowOrder", QueryType.Int64, isNullable: false);
    var sourceQuery = new AggregationQuery("summary")
    {
        SourcePredicate = new Predicate.Equal(lowOrder, QueryConstant.Of(lowOrder, 2L))
    };
    var source = session.Aggregate(sourceQuery);
    Require(source.Rows.Count == 1 && Equals(source.Rows[0]["total"], 7L),
        "The packed public API did not filter source rows before reduction.");
    Require(!string.IsNullOrWhiteSpace(source.ShapeFingerprint) && !string.IsNullOrWhiteSpace(source.ValueFingerprint),
        "The packed public API did not expose aggregation fingerprints.");

    var changedSource = session.Aggregate(new AggregationQuery("summary")
    {
        SourcePredicate = new Predicate.Equal(lowOrder, QueryConstant.Of(lowOrder, 1L))
    });
    Require(source.ShapeFingerprint == changedSource.ShapeFingerprint,
        "The packed public API changed aggregation shape identity when only a source literal changed.");
    Require(source.ValueFingerprint != changedSource.ValueFingerprint,
        "The packed public API did not bind source literal values into aggregation identity.");

    var post = session.Aggregate(new AggregationQuery("summary")
    {
        PostPredicate = new AggregationPredicate.Comparison(
            "total", AggregationPredicateOperator.Equal, [18L])
    });
    Require(post.Rows.Count == 1 && Equals(post.Rows[0]["total"], 18L),
        "The packed public API post predicate did not observe the reduced group.");
}

static void RunTimeBucketJourney(IStorageProviderConnection connection)
{
    var unit = new KernelStorageUnit
    {
        Id = new StorageUnitId("public_time_bucket"),
        Name = "public_time_bucket",
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, MaxLength = 32, IsNullable = false },
            new() { Name = "createdAt", Type = PortableType.DateTimeOffset },
            new() { Name = "amount", Type = PortableType.Int64 }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        AggregationProfiles =
        [
            new AggregationProfile
            {
                Name = "hourly",
                GroupByExpressions = [AggregationGroup.TimeBucket.FixedUtc("bucket", "createdAt", TimeSpan.FromHours(1))],
                Aggregates = [new Aggregate.Count("count"), new Aggregate.Sum("total", "amount")]
            }
        ]
    };
    Require(connection.Schema.Apply(unit).Applied, "The package-only time-bucket schema did not apply.");
    var session = connection.OpenSession(unit, StorageAccess.Global);
    var from = new DateTimeOffset(2026, 8, 16, 10, 30, 0, TimeSpan.Zero);
    Require(session.Insert(new StorageValues(new Dictionary<string, object?>
    {
        ["id"] = "1", ["createdAt"] = from, ["amount"] = 7L
    })).Status == WriteOutcomeStatus.Inserted, "The time-bucket source row did not insert.");
    var result = session.Aggregate(new AggregationQuery("hourly")
    {
        TimeRange = new AggregationTimeRange(from, from.AddHours(1))
    });
    Require(result.Rows.Count == 1 && Equals(result.Rows[0]["total"], 7L),
        "The clean-room consumer did not execute the public time-bucket profile.");
}

static void RunFailureJourneys(IStorageProviderConnection connection)
{
    var overlongName = new string('u', PortabilityValidator.MaximumPortableIdentifierLength + 2);
    var forgedOverlong = new KernelStorageUnit
    {
        Id = new StorageUnitId("logical.id/with spaces and punctuation"),
        Name = overlongName,
        Columns = [new ColumnDefinition { Name = "id", Type = PortableType.Guid, IsNullable = false }],
        Key = new KeyDefinition { Columns = ["id"] }
    };
    var overlongDiagnostic = PortabilityValidator.Validate(forgedOverlong).Refusals.Single(
        refusal => refusal.Code == "GW-PORT-010");
    Require(overlongDiagnostic.Path == "name" &&
            overlongDiagnostic.Message.Contains(overlongName, StringComparison.Ordinal) &&
            overlongDiagnostic.Message.Contains("at most 63 ASCII bytes", StringComparison.Ordinal) &&
            overlongDiagnostic.Message.Contains("shorter", StringComparison.OrdinalIgnoreCase),
        "The packed public API did not expose the stable overlong physical-name diagnostic.");
    try
    {
        connection.Schema.Apply(forgedOverlong);
        throw new InvalidOperationException("The provider admitted an overlong physical storage-unit name.");
    }
    catch (InvalidOperationException exception)
    {
        Require(exception.Message.Contains("GW-PORT-010", StringComparison.Ordinal) &&
                exception.Message.Contains("at name", StringComparison.Ordinal) &&
                exception.Message.Contains(overlongName, StringComparison.Ordinal) &&
                exception.Message.Contains("at most 63 ASCII bytes", StringComparison.Ordinal) &&
                exception.Message.Contains("shorter", StringComparison.OrdinalIgnoreCase),
            "The provider did not refuse the forged overlong name before schema I/O.");
    }

    var forgedMalformedIndex = new KernelStorageUnit
    {
        Id = new StorageUnitId("logical.index.id/with spaces"),
        Name = "valid_unit",
        Columns = [new ColumnDefinition { Name = "id", Type = PortableType.Guid, IsNullable = false }],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes = [new IndexDefinition { Name = "by.id", Columns = [new IndexColumn("id")] }]
    };
    var malformedIndexDiagnostic = PortabilityValidator.Validate(forgedMalformedIndex).Refusals.Single(
        refusal => refusal.Code == "GW-PORT-010");
    Require(malformedIndexDiagnostic.Path == "indexes[0].name" &&
            malformedIndexDiagnostic.Message.Contains("by.id", StringComparison.Ordinal),
        "The packed public API did not expose a structural malformed-index diagnostic path.");

    try
    {
        _ = RecordTable.For<JsonRecord>("json_failure")
            .Key(row => row.Id)
            .Index("by_payload", row => row.Payload)
            .Build();
        throw new InvalidOperationException("The declaration accepted an index over JSON.");
    }
    catch (StorageDeclarationException exception)
    {
        Require(exception.Diagnostics.Any(diagnostic => diagnostic.Code == "GW-DECL-INDEX-003"), "The declaration diagnostic did not identify the JSON index rule.");
        Require(exception.Message.Contains("Leave the JSON column unindexed", StringComparison.Ordinal), "The declaration diagnostic did not explain the corrective action.");
    }

    var plain = RecordTable.For<PlainCustomer>("plain_customers").Key(row => row.Id).Build();
    var plainRecords = plain.Open(connection);
    try
    {
        _ = plainRecords.Insert(new PlainCustomer(Guid.NewGuid(), "plain@example.test"), RecordWriteOptions.IfVersion(1));
        throw new InvalidOperationException("A version precondition was accepted without optimistic concurrency.");
    }
    catch (InvalidOperationException exception)
    {
        Require(exception.Message.Contains("Declare .OptimisticConcurrency() before using RecordWriteOptions.IfVersion(...).", StringComparison.Ordinal), "The concurrency diagnostic did not explain the exact declaration action.");
    }

    var appliedShape = RecordTable.For<FoldedCustomer>("folded_customers")
        .Key(row => row.Id)
        .Column(row => row.Email, column => column.MaxLength(320).Required())
        .Build();
    Require(connection.Schema.Apply(appliedShape.Definition).Applied, "The schema-drift baseline did not apply.");

    var folded = RecordTable.For<FoldedCustomer>("folded_customers")
        .Key(row => row.Id)
        .Column(row => row.Email, column => column.MaxLength(320).Required().Collation(PortableCollation.OrdinalIgnoreCase))
        .Index("by_email", row => row.Email)
        .Build();
    try
    {
        _ = folded.Open(connection);
        throw new InvalidOperationException("Schema drift was admitted to a public session.");
    }
    catch (InvalidOperationException exception)
    {
        Require(exception.Message.Contains("rebuild", StringComparison.OrdinalIgnoreCase), "The schema-drift diagnostic did not explain that the derived search key must be rebuilt.");
    }
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

public sealed record Customer(Guid Id, string Email, string Name);
public sealed record Note(Guid Id, string CustomerId, string Body);
public sealed record PlainCustomer(Guid Id, string Email);
public sealed record FoldedCustomer(Guid Id, string Email);
public sealed record JsonRecord(Guid Id, object Payload);

public sealed class PublicAccessObserver : IStorageAccessObserver
{
    public List<StorageAccessEvent> Events { get; } = [];

    public void Observe(StorageAccessEvent accessEvent) => Events.Add(accessEvent);
}
