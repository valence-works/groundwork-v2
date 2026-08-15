using Groundwork.Kernel;
using Groundwork.Testing;

namespace Groundwork.Testing.SelfTests;

public sealed class InMemoryProviderTests
{
    [Fact]
    public void Full_contract_runs_through_an_external_factory()
    {
        var report = ConformanceSuite.Run(new ExternalFactory(), "memory://contract");

        Assert.True(report.Passed, string.Join(Environment.NewLine,
            report.Failures.Select(failure => $"{failure.Name}: {failure.Failure}")));
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
        Assert.Equal(["by-value", "unique-value"], indexes.Select(index => index.Name));
        Assert.Contains(indexes, index => index.IsUnique && index.Name == "unique-value");
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
                Name = "by-added",
                Columns = [new IndexColumn("added")]
            }]
        };

        var diff = connection.Schema.Diff(additive);
        Assert.Contains(diff.Changes, change => change.Kind == SchemaChangeKind.AddColumn && change.Identity == "added");
        Assert.Contains(diff.Changes, change => change.Kind == SchemaChangeKind.CreateIndex && change.Identity == "by-added");
        connection.Schema.Apply(additive);
        Assert.True(connection.Schema.Diff(additive).IsEmpty);

        var changedKey = additive with { Key = new KeyDefinition { Columns = ["value"] } };
        Assert.Throws<SchemaConflictException>(() => connection.Schema.Diff(changedKey));
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
                .Select(index => index.Name == "by-value" ? index with { SchemaVersion = 2 } : index)
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
            globalSession.Update(TestingFixture.Values("g", "changed"), WriteOptions.ForVersion(1)));

        var scopedInsert = scopedSession.Insert(TestingFixture.Values("s", "first"));
        Assert.Equal(1, scopedInsert.Version);
        Assert.Equal(WriteOutcomeStatus.Updated,
            scopedSession.Update(TestingFixture.Values("s", "second"),
                WriteOptions.ForVersion(scopedInsert.Version!.Value)).Status);
        Assert.Equal(WriteOutcomeStatus.ConcurrencyConflict,
            scopedSession.Update(TestingFixture.Values("s", "stale"), WriteOptions.ForVersion(1)).Status);
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
            new BatchWriteOptions { MaxRowsPerFlush = 100 },
            unit);
        work.Stage(RowWrite.Upsert(unit, TestingFixture.Values("same", "first")));
        work.Stage(RowWrite.Upsert(unit, TestingFixture.Values("same", "last")));

        var staged = work.OpenSession(unit).Read(TestingFixture.Key("same"));
        Assert.Equal("last", staged!.Values.Values["value"]);

        var summary = work.CommitWithOutcomes();
        Assert.Equal(2, summary.Submitted);
        Assert.All(summary.Outcomes, item => Assert.True(item.Outcome.Succeeded));
        Assert.Equal("last", connection.OpenSession(unit, StorageAccess.Global)
            .Read(TestingFixture.Key("same"))!.Values.Values["value"]);
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
            new BatchWriteOptions { MaxRowsPerFlush = 2 },
            unit);
        work.Stage(RowWrite.Insert(unit, TestingFixture.Values("one", "one")));
        work.Stage(RowWrite.Insert(unit, TestingFixture.Values("two", "two")));
        work.Stage(RowWrite.Insert(unit, TestingFixture.Values("three", "three")));

        var summary = await work.CommitWithOutcomesAsync();
        Assert.Equal(3, summary.Submitted);
        Assert.True(summary.IsSuccessful);
    }

    [Fact]
    public void Batched_capabilities_are_advertised_with_stable_descriptors()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://batch-capabilities");

        Assert.Contains(connection.Capabilities,
            descriptor => descriptor.Id == BatchWriteCapabilities.StagedUnitOfWork);
        Assert.Contains(connection.Capabilities,
            descriptor => descriptor.Id == BatchWriteCapabilities.PerRowOutcomes);
    }

    [Fact]
    public void Batched_failure_names_unit_key_and_status()
    {
        var factory = new InMemoryProviderFactory();
        using var connection = factory.Create("memory://batched-failure");
        var unit = TestingFixture.GlobalUnit("batched-failure");
        connection.Schema.Apply(unit);
        using var work = connection.BeginUnitOfWork(StorageAccess.Global, unit);
        work.Stage(RowWrite.Insert(unit, TestingFixture.Values("one", "first", "same")));
        work.Stage(RowWrite.Insert(unit, TestingFixture.Values("two", "second", "same")));

        var error = Assert.Throws<BatchWriteException>(() => work.CommitWithOutcomes());
        Assert.Contains("batched-failure", error.Message, StringComparison.Ordinal);
        Assert.Contains("id=two", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(WriteOutcomeStatus.UniqueViolation), error.Message, StringComparison.Ordinal);
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
}
