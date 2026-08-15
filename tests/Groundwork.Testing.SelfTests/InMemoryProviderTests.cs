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
}
