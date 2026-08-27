using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Testing;
using Groundwork.Store;

namespace Groundwork.Testing.SelfTests;

public sealed class InMemoryProviderTests
{
    [Fact]
    public void A_63_byte_storage_unit_name_applies_without_provider_rewriting()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://physical-name-boundary");
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

    [Fact]
    public void Aggregation_profile_only_drift_is_reported_and_retained()
    {
        var initial = AggregationUnit(1_000);
        using var connection = new InMemoryProviderFactory().Create("memory://aggregation-profile-drift");

        Assert.True(connection.Schema.Apply(initial).Applied);
        Assert.False(connection.Schema.Apply(initial).Applied);

        var changed = AggregationUnit(2_000);
        var diff = connection.Schema.Diff(changed);

        Assert.Contains(diff.Changes, change =>
            change.Kind == SchemaChangeKind.UpdateAggregationProfile && change.Identity == "summary");
        Assert.True(connection.Schema.Apply(changed).Applied);
        Assert.False(connection.Schema.Apply(changed).Applied);
    }

    [Fact]
    public void InMemory_schema_admission_validates_aggregation_profiles_before_persistence()
    {
        var invalid = AggregationUnit(1_000) with
        {
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
        using var connection = new InMemoryProviderFactory().Create("memory://aggregation-invalid-admission");

        var exception = Assert.Throws<AggregationValidationException>(() => connection.Schema.Apply(invalid));

        Assert.Contains(exception.Errors, error => error.Code == "GW-AGG-TYPE-001");
        Assert.Empty(connection.Catalog.ReadIndexes(invalid.Id));
    }

    private static StorageUnit AggregationUnit(int maxGroups) => new()
    {
        Id = new StorageUnitId("aggregation-profile-drift"),
        Name = "aggregation_profile_drift",
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
            new ColumnDefinition { Name = "group", Type = PortableType.String },
            new ColumnDefinition { Name = "amount", Type = PortableType.Int64 },
            new ColumnDefinition { Name = "flag", Type = PortableType.Boolean }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        AggregationProfiles =
        [
            new AggregationProfile
            {
                Name = "summary",
                GroupByColumns = ["group"],
                Aggregates = [new Aggregate.Sum("total", "amount")],
                MaxGroups = maxGroups
            }
        ]
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("stale-search-key-v0")]
    [InlineData("prefix-groundwork-ascii-lower-v1-suffix")]
    public void Search_key_query_mapping_refuses_unknown_or_malformed_algorithm_ids(string? algorithmId)
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("malformed-search-key-mapping"),
            Name = "malformed_search_key_mapping",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new ColumnDefinition { Name = "status", Type = PortableType.String, Collation = PortableCollation.OrdinalIgnoreCase },
                new ColumnDefinition { Name = "__groundwork_search_status", Type = PortableType.String }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            DerivedColumns =
            [
                new DerivedColumnDefinition
                {
                    Name = "__groundwork_search_status",
                    SourceColumn = "status",
                    Projection = PortableProjection.BoundarySearchKey,
                    AlgorithmId = algorithmId
                }
            ]
        };

        var failure = Assert.Throws<InvalidOperationException>(() => SearchKeyQueryMappings.For(unit));

        Assert.Contains("algorithm", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rebuild", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Folded_starts_with_is_served_by_the_hidden_key_and_matches_null_contract()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("q9-folded"),
            Name = "q9_folded",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new ColumnDefinition { Name = "status", Type = PortableType.String, MaxLength = 32, Collation = PortableCollation.OrdinalIgnoreCase }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Indexes = [new IndexDefinition { Name = "ix_status", Columns = [new IndexColumn("status")] }]
        };
        using var connection = new InMemoryProviderFactory().Create("memory://q9-folded");
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Insert(new StorageValues(new Dictionary<string, object?> { ["id"] = 1, ["status"] = "Open" }));
        session.Insert(new StorageValues(new Dictionary<string, object?> { ["id"] = 2, ["status"] = "other" }));
        session.Insert(new StorageValues(new Dictionary<string, object?> { ["id"] = 3, ["status"] = null }));
        session.Update(new StorageValues(new Dictionary<string, object?> { ["id"] = 1 }));
        var stored = session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = 1 }));
        Assert.NotNull(stored);
        Assert.DoesNotContain(SearchKeyProjection.ColumnName("status"), stored!.Values.Values.Keys);

        var status = new ColumnRef(new TableId(unit.Name), "status", QueryType.String, true, 32, stringComparison: QueryStringComparisonPolicy.AsciiIgnoreCase);
        var request = new QueryRequest(new TableId(unit.Name), new Predicate.StartsWith(status, "OP"), [], Projection.All, Paging.None);
        var result = session.Query(request);

        Assert.Equal([1], result.Rows.Select(row => Assert.IsType<int>(row["id"])));
        Assert.Equal(["Open"], result.Rows.Select(row => Assert.IsType<string>(row["status"])));
        Assert.Contains(connection.Catalog.ReadIndexes(unit.Id), index => index.Columns.Single().Column == "__groundwork_search_status");

        var forged = new ColumnRef(new TableId(unit.Name), "status", QueryType.String, true, 32,
            stringComparison: QueryStringComparisonPolicy.Ordinal);
        var failure = Assert.Throws<QueryRenderException>(() => session.Query(new QueryRequest(
            new TableId(unit.Name), new Predicate.StartsWith(forged, "OP"), [], Projection.All, Paging.None)));
        Assert.Equal("GW-QUERY-031", failure.Code);
    }

    [Fact]
    public void Required_folded_partial_updates_preserve_values_through_direct_and_batched_paths()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("q9-required-folded-partial"),
            Name = "q9_required_folded_partial",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new ColumnDefinition
                {
                    Name = "status",
                    Type = PortableType.String,
                    IsNullable = false,
                    MaxLength = 32,
                    Collation = PortableCollation.OrdinalIgnoreCase
                }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Indexes = [new IndexDefinition { Name = "by_status", Columns = [new IndexColumn("status")] }]
        };
        using var connection = new InMemoryProviderFactory().Create("memory://q9-required-folded-partial");
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Insert(new StorageValues(new Dictionary<string, object?> { ["id"] = 1, ["status"] = "Open" }));

        Assert.Equal(WriteOutcomeStatus.Updated, session.Update(
            new StorageValues(new Dictionary<string, object?> { ["id"] = 1 })).Status);
        using (var batch = connection.BeginUnitOfWork(StorageAccess.Global, unit))
        {
            batch.Stage(RowWrite.Update(unit,
                new StorageValues(new Dictionary<string, object?> { ["id"] = 1 })));
            Assert.Equal(1, batch.Commit().Succeeded);
        }

        var stored = session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = 1 }));
        Assert.Equal("Open", stored!.Values.Values["status"]);
        Assert.DoesNotContain(SearchKeyProjection.ColumnName("status"), stored.Values.Values.Keys);
        var status = new ColumnRef(new TableId(unit.Name), "status", QueryType.String, false, 32,
            stringComparison: QueryStringComparisonPolicy.AsciiIgnoreCase);
        var result = session.Query(new QueryRequest(new TableId(unit.Name),
            new Predicate.StartsWith(status, "op"), [], Projection.All, Paging.None));
        Assert.Equal([1], result.Rows.Select(row => Assert.IsType<int>(row["id"])));

        var missingRequired = Assert.Throws<ArgumentException>(() => session.Upsert(
            new StorageValues(new Dictionary<string, object?> { ["id"] = 2 })));
        Assert.Contains("status", missingRequired.Message, StringComparison.Ordinal);
        Assert.Null(session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = 2 })));
    }

    [Fact]
    public void Adding_folding_backfills_existing_rows_and_retargets_the_existing_index()
    {
        var initial = new StorageUnit
        {
            Id = new StorageUnitId("q9-folded-migration"),
            Name = "q9_folded_migration",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new ColumnDefinition { Name = "name", Type = PortableType.String, MaxLength = 32, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Indexes = [new IndexDefinition { Name = "ix_name", Columns = [new IndexColumn("name")] }]
        };
        var folded = initial with
        {
            Columns = [.. initial.Columns.Select(column =>
                column.Name == "name"
                    ? column with { Collation = PortableCollation.OrdinalIgnoreCase }
                    : column)]
        };
        using var connection = new InMemoryProviderFactory().Create("memory://q9-folded-migration");
        connection.Schema.Apply(initial);
        connection.OpenSession(initial, StorageAccess.Global).Insert(
            new StorageValues(new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Open" }));

        var migration = connection.Schema.Apply(folded);
        Assert.Contains(migration.Diff.Changes,
            change => change.Kind == SchemaChangeKind.CreateIndex && change.Identity == "ix_name");

        var session = connection.OpenSession(folded, StorageAccess.Global);
        var name = new ColumnRef(new TableId(folded.Name), "name", QueryType.String, false, 32,
            stringComparison: QueryStringComparisonPolicy.AsciiIgnoreCase);
        var result = session.Query(new QueryRequest(
            new TableId(folded.Name), new Predicate.StartsWith(name, "op"), [], Projection.All, Paging.None));

        Assert.Equal([1], result.Rows.Select(row => row["id"]));
        Assert.Equal("__groundwork_search_name",
            Assert.Single(connection.Catalog.ReadIndexes(folded.Id), index => index.Name == "ix_name").Columns.Single().Column);
    }

    [Fact]
    public void Full_contract_runs_through_an_external_factory()
    {
        var report = ConformanceSuite.Run(new ExternalFactory(), "memory://contract");

        Assert.True(report.Passed, string.Join(Environment.NewLine,
            report.Failures.Select(failure => $"{failure.Name}: {failure.Failure}")));
    }

    [Fact]
    public async Task Full_contract_runs_on_the_async_surface()
    {
        var report = await ConformanceSuite.RunAsync(new ExternalFactory(), "memory://contract-async");

        Assert.True(report.Passed, string.Join(Environment.NewLine,
            report.Failures.Select(failure => $"{failure.Name}: {failure.Failure}")));
        Assert.Contains(report.Checks, check => check.Name == "cancellation is refused before provider work");
    }

    [Fact]
    public async Task Async_surface_proves_every_check_the_sync_surface_proves()
    {
        var synchronous = ConformanceSuite.Run(new ExternalFactory(), "memory://contract-parity-sync");
        var asynchronous = await ConformanceSuite.RunAsync(new ExternalFactory(), "memory://contract-parity-async");

        Assert.Subset(
            asynchronous.Checks.Select(check => check.Name).ToHashSet(StringComparer.Ordinal),
            synchronous.Checks.Select(check => check.Name).ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void Catalog_is_read_from_provider_state_and_schema_apply_is_idempotent()
    {
        var factory = new InMemoryProviderFactory();
        using var connection = factory.Create("memory://catalog");
        var unit = TestingFixture.GlobalUnit();

        var first = connection.Schema.Apply(unit);
        var second = connection.Schema.Apply(unit);
        var indexes = connection.Catalog.ReadIndexes(unit.Id);

        Assert.False(first.IsNoOp);
        Assert.True(second.IsNoOp);
        Assert.Equal(["by_value", "unique_value"], indexes.Select(index => index.Name));
        Assert.Contains(indexes, index => index.IsUnique && index.Name == "unique_value");
    }

    [Fact]
    public void Schema_diff_is_additive_and_rejects_non_additive_key_changes()
    {
        var factory = new InMemoryProviderFactory();
        using var connection = factory.Create("memory://schema-diff");
        var initial = TestingFixture.GlobalUnit("schema-diff");
        connection.Schema.Apply(initial);
        var additive = initial with
        {
            Columns = [.. initial.Columns, new ColumnDefinition { Name = "added", Type = PortableType.Int32 }],
            Indexes = [.. initial.Indexes, new IndexDefinition
            {
                Name = "by_added",
                Columns = [new IndexColumn("added")]
            }]
        };

        var diff = connection.Schema.Diff(additive);
        Assert.Contains(diff.Changes, change => change.Kind == SchemaChangeKind.AddColumn && change.Identity == "added");
        Assert.Contains(diff.Changes, change => change.Kind == SchemaChangeKind.CreateIndex && change.Identity == "by_added");
        connection.Schema.Apply(additive);
        Assert.True(connection.Schema.Diff(additive).IsEmpty);

        var changedKey = additive with { Key = new KeyDefinition { Columns = ["value"] } };
        Assert.Throws<SchemaConflictException>(() => connection.Schema.Diff(changedKey));
    }

    [Fact]
    public void Schema_apply_refuses_retention_partition_layout_drift()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://retention-partition-drift");
        var name = "retention_partition_drift";
        var initial = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, MaxLength = 16, IsNullable = false },
                new() { Name = "ordering", Type = PortableType.Int64, IsNullable = false },
                new() { Name = "a_b", Type = PortableType.String, MaxLength = 16, IsNullable = false },
                new() { Name = "a", Type = PortableType.String, MaxLength = 16, IsNullable = false },
                new() { Name = "b", Type = PortableType.String, MaxLength = 16, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Retention = new RetentionDeclaration
            {
                KeepNewest = 3,
                OrderColumn = "ordering",
                PartitionColumns = ["a_b"]
            }
        };
        Assert.True(connection.Schema.Apply(initial).Applied);
        var drifted = initial with
        {
            Retention = initial.Retention! with { PartitionColumns = ["a", "b"] }
        };

        var conflict = Assert.Throws<SchemaConflictException>(() => connection.Schema.Apply(drifted));
        Assert.Contains("retention", conflict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(connection.Schema.Diff(initial).IsEmpty);
    }

    [Fact]
    public void Schema_diff_identity_includes_scope_version_defaults_and_index_version()
    {
        var factory = new InMemoryProviderFactory();
        using var connection = factory.Create("memory://schema-identity");
        var baseUnit = TestingFixture.GlobalUnit("schema-identity");
        var initial = baseUnit with
        {
            Columns = baseUnit.Columns
                .Select(column => column.Name == "value"
                    ? column with { Default = new PortableDefault(null) }
                    : column)
                .ToArray()
        };
        connection.Schema.Apply(initial);

        var absentDefault = initial with
        {
            Columns = initial.Columns
                .Select(column => column.Name == "value" ? column with { Default = null } : column)
                .ToArray()
        };
        Assert.Throws<SchemaConflictException>(() => connection.Schema.Diff(absentDefault));

        var changedIndex = initial with
        {
            Indexes = initial.Indexes
                .Select(index => index.Name == "by_value" ? index with { SchemaVersion = 2 } : index)
                .ToArray()
        };
        Assert.Throws<SchemaConflictException>(() => connection.Schema.Diff(changedIndex));

        var changedScope = initial with { Scope = ScopePolicy.Scoped };
        Assert.Throws<SchemaConflictException>(() => connection.Schema.Diff(changedScope));
    }

    [Fact]
    public void CRUD_reports_write_outcomes_and_enforces_unique_indexes()
    {
        var factory = new InMemoryProviderFactory();
        using var connection = factory.Create("memory://crud");
        var unit = TestingFixture.GlobalUnit();
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);

        Assert.Equal(WriteOutcomeStatus.Inserted,
            session.Insert(TestingFixture.Values("one", "first", "same")).Status);
        Assert.Equal(WriteOutcomeStatus.UniqueViolation,
            session.Insert(TestingFixture.Values("two", "second", "same")).Status);
        Assert.Equal(WriteOutcomeStatus.Updated,
            session.Update(TestingFixture.Values("one", "changed", "same")).Status);
        Assert.Equal(WriteOutcomeStatus.Upserted,
            session.Upsert(TestingFixture.Values("two", "second", "other")).Status);
        Assert.Equal(WriteOutcomeStatus.Deleted,
            session.Delete(TestingFixture.Key("two")).Status);
        Assert.Equal(WriteOutcomeStatus.NotFound,
            session.Delete(TestingFixture.Key("two")).Status);
    }

    [Fact]
    public void Optimistic_concurrency_is_declared_and_absent_otherwise()
    {
        var factory = new InMemoryProviderFactory();
        using var connection = factory.Create("memory://versions");
        var global = TestingFixture.GlobalUnit("versions-global");
        var scoped = TestingFixture.ScopedUnit("versions-scoped");
        connection.Schema.Apply(global);
        connection.Schema.Apply(scoped);
        var globalSession = connection.OpenSession(global, StorageAccess.Global);
        var scopedSession = connection.OpenSession(scoped, StorageAccess.Scoped(new StorageScope("a")));

        var inserted = globalSession.Insert(TestingFixture.Values("g", "first"));
        Assert.Null(inserted.Version);
        Assert.Null(globalSession.Read(TestingFixture.Key("g"))!.Version);
        Assert.Throws<InvalidOperationException>(() =>
            globalSession.Update(TestingFixture.Values("g", "changed"), WriteOptions.IfVersion(1)));

        var scopedInsert = scopedSession.Insert(TestingFixture.Values("s", "first"));
        Assert.Equal(1, scopedInsert.Version);
        Assert.Equal(WriteOutcomeStatus.Updated,
            scopedSession.Update(TestingFixture.Values("s", "second"),
                WriteOptions.IfVersion(scopedInsert.Version!.Value)).Status);
        Assert.Equal(WriteOutcomeStatus.ConcurrencyConflict,
            scopedSession.Update(TestingFixture.Values("s", "stale"), WriteOptions.IfVersion(1)).Status);
    }

    [Fact]
    public void Scope_mismatch_is_rejected_and_scopes_are_isolated()
    {
        var factory = new InMemoryProviderFactory();
        using var connection = factory.Create("memory://scope");
        var global = TestingFixture.GlobalUnit("scope-global");
        var scoped = TestingFixture.ScopedUnit("scope-scoped");
        connection.Schema.Apply(global);
        connection.Schema.Apply(scoped);

        Assert.Throws<InvalidOperationException>(() =>
            connection.OpenSession(scoped, StorageAccess.Global));
        Assert.Throws<InvalidOperationException>(() =>
            connection.OpenSession(global, StorageAccess.Scoped(new StorageScope("a"))));

        var first = connection.OpenSession(scoped, StorageAccess.Scoped(new StorageScope("a")));
        var second = connection.OpenSession(scoped, StorageAccess.Scoped(new StorageScope("b")));
        first.Insert(TestingFixture.Values("same", "a"));
        second.Insert(TestingFixture.Values("same", "b"));

        Assert.Equal("a", first.Read(TestingFixture.Key("same"))!.Values.Values["value"]);
        Assert.Equal("b", second.Read(TestingFixture.Key("same"))!.Values.Values["value"]);
    }

    [Fact]
    public void Privileged_cross_scope_access_is_audited_query_only_and_scope_preserving()
    {
        var factory = new InMemoryProviderFactory();
        using var connection = factory.Create("memory://privileged-cross-scope");
        var unit = TestingFixture.ScopedUnit("privileged-cross-scope");
        connection.Schema.Apply(unit);

        connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("tenant-a")))
            .Insert(TestingFixture.Values("same", "shared"));
        connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("tenant-b")))
            .Insert(TestingFixture.Values("same", "shared"));

        var observer = new RecordingAccessObserver();
        var access = StorageAccess.PrivilegedAcrossScopes(
            new StorageAccessAudit("elsa-recovery", "recover-stalled-workflows", observer));
        var session = connection.OpenSession(unit, access);
        var table = new TableId(unit.Name);
        var request = new QueryRequest(
            table,
            Predicate.AlwaysTrue.Instance,
            [],
            Projection.All,
            Paging.Keyset(1),
            ResultShape.TotalCount.Instance);

        Assert.True(session.Access.IsPrivilegedAcrossScopes);
        Assert.Equal("elsa-recovery", session.Access.Audit!.Identity);
        Assert.Equal("recover-stalled-workflows", session.Access.Audit.Purpose);
        Assert.Throws<InvalidOperationException>(() => session.Read(TestingFixture.Key("same")));
        Assert.Throws<InvalidOperationException>(() =>
            session.Insert(TestingFixture.Values("other", "refused")));
        Assert.Throws<InvalidOperationException>(() => session.Query(request));
        Assert.Throws<InvalidOperationException>(() => session.Inspect());
        Assert.Throws<InvalidOperationException>(() => session.ApplyRetention());

        var first = session.QueryAcrossScopes(request);

        var auditEvent = Assert.Single(observer.Events);
        Assert.Equal(unit.Id, auditEvent.Unit);
        Assert.Equal("query-across-scopes", auditEvent.Operation);
        Assert.Equal("elsa-recovery", auditEvent.Identity);
        Assert.Equal("recover-stalled-workflows", auditEvent.Purpose);
        Assert.Equal(2, first.TotalCount);
        Assert.Single(first.Rows);
        Assert.Equal("tenant-a", first.Rows[0].Scope.Value);
        Assert.Equal("same", first.Rows[0].Values["id"]);
        Assert.NotNull(first.NextContinuationToken);
        AssertOpaque(first.NextContinuationToken!, "tenant-a", "tenant-b", "elsa-recovery", "recover-stalled-workflows");

        var second = session.QueryAcrossScopes(new QueryRequest(
            table,
            request.Where,
            request.Order,
            request.Projection,
            Paging.Continuation(first.NextContinuationToken!, 1),
            request.Result));
        Assert.Single(second.Rows);
        Assert.Equal("tenant-b", second.Rows[0].Scope.Value);

        var differentAudit = connection.OpenSession(unit, StorageAccess.PrivilegedAcrossScopes(
            new StorageAccessAudit("other-operator", "recover-stalled-workflows")));
        var tokenFailure = Assert.Throws<QueryRenderException>(() => differentAudit.QueryAcrossScopes(
            new QueryRequest(table, request.Where, request.Order, request.Projection,
                Paging.Continuation(first.NextContinuationToken!, 1), request.Result)));
        Assert.Equal("GW-QUERY-013", tokenFailure.Code);
    }

    private sealed class RecordingAccessObserver : IStorageAccessObserver
    {
        public List<StorageAccessEvent> Events { get; } = [];

        public void Observe(StorageAccessEvent accessEvent) => Events.Add(accessEvent);
    }

    [Theory]
    [InlineData(null, "purpose")]
    [InlineData("", "purpose")]
    [InlineData("identity", null)]
    [InlineData("identity", " ")]
    public void Privileged_cross_scope_audit_rejects_blank_identity_or_purpose(
        string? identity,
        string? purpose)
    {
        Assert.ThrowsAny<ArgumentException>(() => new StorageAccessAudit(identity!, purpose!));
    }

    [Fact]
    public void Privileged_cross_scope_audit_rejects_malformed_utf16()
    {
        var high = new string('\uD800', 1);
        var low = new string('\uDC00', 1);
        Assert.Throws<ArgumentException>(() => new StorageAccessAudit(high, "purpose"));
        Assert.Throws<ArgumentException>(() => new StorageAccessAudit("identity", low));
    }

    [Theory]
    [InlineData("__groundwork_scope")]
    [InlineData("__groundwork_scope_token")]
    public void Provider_owned_cross_scope_columns_cannot_be_declared_by_applications(string reservedName)
    {
        using var connection = new InMemoryProviderFactory().Create("memory://reserved-cross-scope-column");
        var unit = TestingFixture.GlobalUnit("reserved-cross-scope-column-" + Guid.NewGuid().ToString("N")) with
        {
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false },
                new ColumnDefinition { Name = reservedName, Type = PortableType.String }
            ]
        };

        var failure = Assert.Throws<ArgumentException>(() => connection.Schema.Apply(unit));

        Assert.Contains("provider-owned", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unit_of_work_commits_and_rolls_back_staged_values()
    {
        var factory = new InMemoryProviderFactory();
        using var connection = factory.Create("memory://uow");
        var unit = TestingFixture.GlobalUnit("uow");
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);

        using (var commit = connection.BeginUnitOfWork(StorageAccess.Global, unit))
        {
            commit.OpenSession(unit).Insert(TestingFixture.Values("committed", "yes"));
            commit.Commit();
        }
        Assert.NotNull(session.Read(TestingFixture.Key("committed")));

        using (var rollback = connection.BeginUnitOfWork(StorageAccess.Global, unit))
        {
            rollback.OpenSession(unit).Insert(TestingFixture.Values("rolled-back", "no"));
            rollback.Rollback();
        }
        Assert.Null(session.Read(TestingFixture.Key("rolled-back")));
    }

    [Fact]
    public void Batched_unit_of_work_coalesces_same_key_and_flushes_before_staged_read()
    {
        var factory = new InMemoryProviderFactory();
        using var connection = factory.Create("memory://batched-uow");
        var unit = TestingFixture.GlobalUnit("batched-uow");
        connection.Schema.Apply(unit);

        using var work = connection.BeginUnitOfWork(
            StorageAccess.Global,
            new BatchWriteOptions { MaxRowsPerFlush = 100, OutcomeMode = BatchOutcomeMode.Exact },
            unit);
        work.Stage(RowWrite.Upsert(unit, TestingFixture.Values("same", "first")));
        work.Stage(RowWrite.Upsert(unit, TestingFixture.Values("same", "last")));

        var staged = work.OpenSession(unit).Read(TestingFixture.Key("same"));
        Assert.Equal("last", staged!.Values.Values["value"]);

        var report = work.CommitWithOutcomes();
        Assert.Equal(2, report.Submitted);
        Assert.Equal(1, report.Applied);
        Assert.Equal(1, report.Superseded);
        var superseded = Assert.Single(report.Outcomes.Where(item => item.IsSuperseded));
        Assert.Equal(1, superseded.WinnerOrdinal);
        Assert.Equal(WriteOutcomeStatus.Upserted, superseded.WinnerEvidence!.Status);
        Assert.Equal(WriteOutcomeStatus.Superseded, superseded.Outcome.Status);
        Assert.Equal("last", connection.OpenSession(unit, StorageAccess.Global)
            .Read(TestingFixture.Key("same"))!.Values.Values["value"]);
    }

    [Fact]
    public void Batched_coalescing_happens_before_mode_and_column_grouping()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batched-mixed-shapes");
        var unit = TestingFixture.GlobalUnit("batched-mixed-shapes");
        connection.Schema.Apply(unit);
        using var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit);

        work.Stage(RowWrite.Insert(unit, TestingFixture.Values("same", "inserted", "unique")));
        work.Stage(RowWrite.Update(unit, new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "same", ["value"] = "updated"
        })));
        work.Stage(RowWrite.Upsert(unit, new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "same", ["value"] = "final"
        })));

        var summary = work.CommitWithOutcomes();

        Assert.Equal(3, summary.Submitted);
        Assert.Equal(1, summary.Applied);
        Assert.Equal(2, summary.Superseded);
        Assert.All(summary.Outcomes.Where(outcome => outcome.IsSuperseded), outcome =>
        {
            Assert.Equal(2, outcome.WinnerOrdinal);
            Assert.Equal(WriteOutcomeStatus.Upserted, outcome.WinnerEvidence!.Status);
        });
        Assert.Equal("final", connection.OpenSession(unit, StorageAccess.Global)
            .Read(TestingFixture.Key("same"))!.Values.Values["value"]);
    }

    [Fact]
    public void Generated_key_declarations_use_collision_free_reference_identity_until_assigned()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("generated-coalescing-identity"),
            Name = "GeneratedCoalescingIdentity",
            Columns =
            [
                new() { Name = "sequence", Type = PortableType.Int64, IsNullable = false, Generation = ColumnGeneration.ProviderSequence }
            ],
            Key = new KeyDefinition { Columns = ["sequence"] }
        };
        var first = RowWrite.Insert(unit, new StorageValues(new Dictionary<string, object?>()));
        var second = RowWrite.Insert(unit, new StorageValues(new Dictionary<string, object?>()));

        Assert.NotSame(first.CoalescingIdentity, second.CoalescingIdentity);
        Assert.Same(first.CoalescingIdentity, first.CoalescingIdentity);
    }

    [Fact]
    public void Batched_generated_key_insert_flushes_before_a_staged_key_read()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://generated-key-read-barrier");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("generated-key-read-barrier"),
            Name = "GeneratedKeyReadBarrier",
            Columns =
            [
                new() { Name = "sequence", Type = PortableType.Int64, IsNullable = false, Generation = ColumnGeneration.ProviderSequence },
                new() { Name = "value", Type = PortableType.String }
            ],
            Key = new KeyDefinition { Columns = ["sequence"] }
        };
        connection.Schema.Apply(unit);
        using var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit);
        work.Stage(RowWrite.Insert(unit, new StorageValues(new Dictionary<string, object?> { ["value"] = "staged" })));

        var staged = work.OpenSession(unit).Read(new StorageKey(
            new Dictionary<string, object?> { ["sequence"] = 1L }));

        Assert.Equal("staged", staged!.Values.Values["value"]);
        var report = work.CommitWithOutcomes();
        Assert.Equal(1L, Assert.Single(report.Outcomes).Outcome.GeneratedValue<long>("sequence"));
    }

    [Fact]
    public void Batched_identity_is_collision_free_for_composite_delimiter_values()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batched-composite-identity");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("batched-composite-identity"),
            Name = "CompositeIdentity",
            Columns =
            [
                new() { Name = "left", Type = PortableType.String, IsNullable = false },
                new() { Name = "right", Type = PortableType.String, IsNullable = false },
                new() { Name = "value", Type = PortableType.String }
            ],
            Key = new KeyDefinition { Columns = ["left", "right"] }
        };
        connection.Schema.Apply(unit);
        var first = new StorageValues(new Dictionary<string, object?>
        {
            ["left"] = "a\u001e", ["right"] = "b", ["value"] = "first"
        });
        var second = new StorageValues(new Dictionary<string, object?>
        {
            ["left"] = "a", ["right"] = "\u001eb", ["value"] = "second"
        });
        using var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit);
        work.Stage(RowWrite.Upsert(unit, first));
        work.Stage(RowWrite.Upsert(unit, second));

        var summary = work.CommitWithOutcomes();

        Assert.Equal(2, summary.Submitted);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        Assert.Equal("first", session.Read(new StorageKey(new Dictionary<string, object?>
        {
            ["left"] = "a\u001e", ["right"] = "b"
        }))!.Values.Values["value"]);
        Assert.Equal("second", session.Read(new StorageKey(new Dictionary<string, object?>
        {
            ["left"] = "a", ["right"] = "\u001eb"
        }))!.Values.Values["value"]);
    }

    [Fact]
    public void Batched_unit_of_work_rejects_reusing_the_same_write_declaration()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batched-duplicate-declaration");
        var unit = TestingFixture.GlobalUnit("batched-duplicate-declaration");
        connection.Schema.Apply(unit);
        using var work = connection.BeginUnitOfWork(StorageAccess.Global, unit);
        var write = RowWrite.Upsert(unit, TestingFixture.Values("same", "same"));
        work.Stage(write);

        var error = Assert.Throws<ArgumentException>(() => work.Stage(write));

        Assert.Contains("new RowWrite declaration", error.Message, StringComparison.Ordinal);
        Assert.True(work.Commit().IsSuccessful);
    }

    [Fact]
    public async Task Batched_unit_of_work_honors_flush_cap_and_async_commit()
    {
        var factory = new InMemoryProviderFactory();
        using var connection = factory.Create("memory://batched-cap");
        var unit = TestingFixture.GlobalUnit("batched-cap");
        connection.Schema.Apply(unit);

        using var work = connection.BeginUnitOfWork(
            StorageAccess.Global,
            new BatchWriteOptions { MaxRowsPerFlush = 2, OutcomeMode = BatchOutcomeMode.Exact },
            unit);
        work.Stage(RowWrite.Insert(unit, TestingFixture.Values("one", "one")));
        work.Stage(RowWrite.Insert(unit, TestingFixture.Values("two", "two")));
        work.Stage(RowWrite.Insert(unit, TestingFixture.Values("three", "three")));

        var report = await work.CommitWithOutcomesAsync();
        Assert.Equal(3, report.Submitted);
        Assert.Equal(3, report.Outcomes.Count);
        Assert.True(report.IsSuccessful);
    }

    [Fact]
    public void Failed_cap_flush_poisoning_prevents_later_commit_or_staging()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batched-poisoned");
        var unit = TestingFixture.GlobalUnit("batched-poisoned");
        connection.Schema.Apply(unit);
        using var work = connection.BeginUnitOfWork(
            StorageAccess.Global,
            new BatchWriteOptions { MaxRowsPerFlush = 2 },
            unit);

        work.Stage(RowWrite.Insert(unit, TestingFixture.Values("one", "one", "duplicate")));
        Assert.Throws<BatchWriteException>(() => work.Stage(
            RowWrite.Insert(unit, TestingFixture.Values("two", "two", "duplicate"))));

        Assert.Throws<InvalidOperationException>(() => work.Stage(
            RowWrite.Insert(unit, TestingFixture.Values("three", "three", "three"))));
        Assert.Throws<InvalidOperationException>(() => work.Commit());
        Assert.Null(connection.OpenSession(unit, StorageAccess.Global).Read(TestingFixture.Key("one")));
    }

    [Fact]
    public void Batched_query_flushes_staged_writes_before_reading_the_unit()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batched-query");
        var unit = TestingFixture.GlobalUnit("batched-query");
        connection.Schema.Apply(unit);
        using var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit);
        work.Stage(RowWrite.Insert(unit, TestingFixture.Values("visible", "staged")));

        var table = new TableId(unit.Name);
        var result = work.OpenSession(unit).Query(new QueryRequest(
            table,
            Predicate.AlwaysTrue.Instance,
            [],
            Projection.All,
            Paging.None));

        Assert.Equal("staged", result.Rows.Single()["value"]);
        Assert.True(work.CommitWithOutcomes().IsSuccessful);
    }

    [Fact]
    public void Aggregate_mode_rejects_exact_commit_after_cap_flush()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batched-aggregate-cap");
        var unit = TestingFixture.GlobalUnit("batched-aggregate-cap");
        connection.Schema.Apply(unit);
        using var work = connection.BeginUnitOfWork(
            StorageAccess.Global,
            new BatchWriteOptions { MaxRowsPerFlush = 1, OutcomeMode = BatchOutcomeMode.Aggregate },
            unit);

        work.Stage(RowWrite.Insert(unit, TestingFixture.Values("one", "one")));

        var error = Assert.Throws<InvalidOperationException>(() => work.CommitWithOutcomes());
        Assert.Contains("aggregate outcomes", error.Message, StringComparison.Ordinal);
        Assert.True(work.Commit().IsSuccessful);
    }

    [Fact]
    public void Aggregate_mode_rejects_exact_commit_after_staged_read_barrier()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batched-aggregate-read");
        var unit = TestingFixture.GlobalUnit("batched-aggregate-read");
        connection.Schema.Apply(unit);
        using var work = connection.BeginUnitOfWork(
            StorageAccess.Global,
            new BatchWriteOptions { OutcomeMode = BatchOutcomeMode.Aggregate },
            unit);
        work.Stage(RowWrite.Insert(unit, TestingFixture.Values("one", "one")));

        Assert.NotNull(work.OpenSession(unit).Read(TestingFixture.Key("one")));
        Assert.Throws<InvalidOperationException>(() => work.CommitWithOutcomes());
        var summary = work.Commit();
        Assert.True(summary.IsSuccessful);
    }

    [Fact]
    public void Aggregate_mode_rejects_exact_commit_after_query_barrier()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batched-aggregate-query");
        var unit = TestingFixture.GlobalUnit("batched-aggregate-query");
        connection.Schema.Apply(unit);
        using var work = connection.BeginUnitOfWork(
            StorageAccess.Global,
            new BatchWriteOptions { OutcomeMode = BatchOutcomeMode.Aggregate },
            unit);
        work.Stage(RowWrite.Insert(unit, TestingFixture.Values("one", "one")));

        var result = work.OpenSession(unit).Query(new QueryRequest(
            new TableId(unit.Name), Predicate.AlwaysTrue.Instance, [], Projection.All, Paging.None));
        Assert.Single(result.Rows);
        Assert.Throws<InvalidOperationException>(() => work.CommitWithOutcomes());
        Assert.True(work.Commit().IsSuccessful);
    }

    [Fact]
    public async Task Default_unit_of_work_uses_aggregate_mode()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batched-default-mode");
        var unit = TestingFixture.GlobalUnit("batched-default-mode");
        connection.Schema.Apply(unit);
        using var work = connection.BeginUnitOfWork(StorageAccess.Global, unit);
        work.Stage(RowWrite.Insert(unit, TestingFixture.Values("one", "one")));

        Assert.Throws<InvalidOperationException>(() => work.CommitWithOutcomes());
        var summary = await work.CommitAsync();
        Assert.True(summary.IsSuccessful);
        Assert.Null(typeof(BatchWriteSummary).GetProperty("Outcomes"));
    }

    [Fact]
    public void Batched_capabilities_are_advertised_with_stable_descriptors()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batch-capabilities");

        Assert.Contains(connection.Capabilities,
            descriptor => descriptor.Id == BatchWriteCapabilities.StagedUnitOfWork);
        Assert.Contains(connection.Capabilities,
            descriptor => descriptor.Id == BatchWriteCapabilities.PerRowOutcomes);
        Assert.Contains(connection.Capabilities,
            descriptor => descriptor.Id == WellKnownCapabilities.AtomicCommit);
    }

    [Fact]
    public void Batched_failure_names_unit_key_and_status()
    {
        var factory = new InMemoryProviderFactory();
        using var connection = factory.Create("memory://batched-failure");
        var unit = TestingFixture.GlobalUnit("batched-failure");
        connection.Schema.Apply(unit);
        using var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit);
        work.Stage(RowWrite.Insert(unit, TestingFixture.Values("one", "first", "same")));
        work.Stage(RowWrite.Insert(unit, TestingFixture.Values("two", "second", "same")));

        var error = Assert.Throws<BatchWriteException>(() => work.CommitWithOutcomes());
        Assert.Contains("batched-failure", error.Message, StringComparison.Ordinal);
        Assert.Contains("id=two", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(WriteOutcomeStatus.UniqueViolation), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Superseded_inputs_are_not_reported_as_provider_failures()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batched-superseded-failure");
        var unit = TestingFixture.GlobalUnit("batched-superseded-failure");
        connection.Schema.Apply(unit);
        connection.OpenSession(unit, StorageAccess.Global).Insert(
            TestingFixture.Values("existing", "existing", "duplicate"));

        using var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit);
        work.Stage(RowWrite.Insert(unit, TestingFixture.Values("target", "first", "first")));
        work.Stage(RowWrite.Insert(unit, TestingFixture.Values("target", "winner", "duplicate")));

        var error = Assert.Throws<BatchWriteException>(() => work.CommitWithOutcomes());
        Assert.Equal(1, error.Message.Split("id=target", StringSplitOptions.None).Length - 1);
        var failure = Assert.Single(error.Outcomes);
        Assert.Equal(RowWriteDisposition.Applied, failure.Disposition);
        Assert.Equal(WriteOutcomeStatus.UniqueViolation, failure.Outcome.Status);
    }

    [Fact]
    public void Unit_of_work_rejects_lost_updates_atomically()
    {
        var factory = new InMemoryProviderFactory();
        using var connection = factory.Create("memory://uow-conflict");
        var unit = TestingFixture.GlobalUnit("uow-conflict");
        connection.Schema.Apply(unit);
        var outside = connection.OpenSession(unit, StorageAccess.Global);
        outside.Insert(TestingFixture.Values("same", "before"));

        using var work = connection.BeginUnitOfWork(StorageAccess.Global, unit);
        work.OpenSession(unit).Update(TestingFixture.Values("same", "staged"));
        outside.Update(TestingFixture.Values("same", "outside"));

        Assert.Throws<InvalidOperationException>(() => work.Commit());
        Assert.Equal("outside", outside.Read(TestingFixture.Key("same"))!.Values.Values["value"]);
        work.Rollback();
    }

    [Fact]
    public void Declared_defaults_are_deep_snapshots()
    {
        var factory = new InMemoryProviderFactory();
        using var connection = factory.Create("memory://default-snapshot");
        var bytes = new byte[] { 1, 2 };
        var items = new List<int> { 3, 4 };
        var nested = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["bytes"] = bytes,
            ["items"] = items
        };
        var baseUnit = TestingFixture.GlobalUnit("default-snapshot");
        var unit = baseUnit with
        {
            Columns = baseUnit.Columns
                .Select(column => column.Name == "value"
                    ? column with { Default = new PortableDefault(nested) }
                    : column)
                .ToArray()
        };

        connection.Schema.Apply(unit);
        bytes[0] = 9;
        items[0] = 8;
        nested["new"] = "mutated";

        var session = connection.OpenSession(unit, StorageAccess.Global);
        var snapshot = session.Unit.Columns.Single(column => column.Name == "value").Default!.Value;
        var dictionary = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(snapshot);
        Assert.Equal(new byte[] { 1, 2 }, Assert.IsType<byte[]>(dictionary["bytes"]));
        Assert.Equal(new object?[] { 3, 4 },
            Assert.IsAssignableFrom<IEnumerable<object?>>(dictionary["items"]).ToArray());
        Assert.False(dictionary.ContainsKey("new"));
    }

    [Fact]
    public void Unsupported_mutable_default_is_rejected_at_snapshot_boundary()
    {
        var factory = new InMemoryProviderFactory();
        using var connection = factory.Create("memory://default-rejection");
        var baseUnit = TestingFixture.GlobalUnit("default-rejection");
        var unit = baseUnit with
        {
            Columns = baseUnit.Columns
                .Select(column => column.Name == "value"
                    ? column with { Default = new PortableDefault(new MutableValue()) }
                    : column)
                .ToArray()
        };

        Assert.Throws<ArgumentException>(() => connection.Schema.Apply(unit));
    }

    [Fact]
    public void Values_and_results_are_defensive_snapshots()
    {
        var factory = new InMemoryProviderFactory();
        using var connection = factory.Create("memory://snapshots");
        var unit = TestingFixture.GlobalUnit("snapshots");
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var source = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = "stable",
            ["value"] = "before",
            ["uniqueValue"] = "stable"
        };
        var values = new StorageValues(source);
        session.Insert(values);
        source["value"] = "after";

        var loaded = session.Read(TestingFixture.Key("stable"));
        Assert.Equal("before", loaded!.Values.Values["value"]);
        var writable = Assert.IsAssignableFrom<IDictionary<string, object?>>(loaded.Values.Values);
        Assert.Throws<NotSupportedException>(() => writable["value"] = "tampered");
    }

    [Fact]
    public void In_memory_query_honors_order_paging_continuation_count_and_budget()
    {
        var factory = new InMemoryProviderFactory();
        using var connection = factory.Create("memory://query-contract");
        var unit = TestingFixture.GlobalUnit("query-contract");
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Insert(TestingFixture.Values("a", "b"));
        session.Insert(TestingFixture.Values("b", "a"));
        session.Insert(TestingFixture.Values("c", "c"));

        var table = new TableId(unit.Name);
        var id = new ColumnRef(table, "id", QueryType.String, isNullable: false);
        var value = new ColumnRef(table, "value", QueryType.String, isNullable: true);
        var options = new QueryRenderOptions(tieBreakColumns: [id]);
        var firstRequest = new QueryRequest(
            table,
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(value, OrderDirection.Ascending, NullOrder.Last)],
            Projection.All,
            Paging.OffsetLimit(1, 1));

        var first = session.Query(firstRequest, options);
        Assert.Equal("b", first.Rows.Single()["value"]);
        Assert.NotNull(first.NextContinuationToken);

        var next = session.Query(new QueryRequest(
            table,
            firstRequest.Where,
            firstRequest.Order,
            firstRequest.Projection,
            Paging.Continuation(first.NextContinuationToken!, 1)), options);
        Assert.Equal("c", next.Rows.Single()["value"]);

        var counted = session.Query(new QueryRequest(
            table,
            firstRequest.Where,
            firstRequest.Order,
            firstRequest.Projection,
            Paging.OffsetLimit(100, 1),
            ResultShape.TotalCount.Instance), options);
        Assert.Empty(counted.Rows);
        Assert.Equal(3, counted.TotalCount);

        var budgetRequest = new QueryRequest(
            table,
            new Predicate.In(value, [QueryConstant.Of(value, "a"), QueryConstant.Of(value, "b")]),
            [],
            Projection.All,
            Paging.None);
        var budgetFailure = Assert.Throws<QueryRenderException>(() => session.Query(budgetRequest, options with { InValueLimit = 1 }));
        Assert.Equal("GW-QUERY-015", budgetFailure.Code);
    }

    private sealed class ExternalFactory : IStorageProviderFactory
    {
        private readonly InMemoryProviderFactory inner = new();

        public IStorageProviderConnection Create(string connectionString) =>
            inner.Create(connectionString);
    }

    private sealed class MutableValue
    {
        public string Text { get; set; } = "mutable";
    }

    private static void AssertOpaque(string token, params string[] forbiddenValues)
    {
        var segments = token.Split('.').Skip(1).Select(segment =>
        {
            var padded = segment.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        });
        var decoded = string.Join("|", segments);
        foreach (var forbidden in forbiddenValues)
            Assert.DoesNotContain(forbidden, decoded, StringComparison.Ordinal);
    }
}
