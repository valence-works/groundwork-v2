using Microsoft.Data.Sqlite;
using Groundwork.Kernel;
using Groundwork.Testing;
using Groundwork.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteProviderTests
{
    [Fact]
    public void Provider_passes_provider_neutral_conformance()
    {
        using var store = TemporaryStore.Create();
        var report = ConformanceSuite.Run(new SqliteProviderFactory(), store.ConnectionString);
        Assert.True(report.Passed, string.Join(Environment.NewLine, report.Checks.Where(check => !check.Passed).Select(check => $"{check.Name}: {check.Failure}")));
    }

    [Fact]
    public void Non_nullable_addition_rebuild_preserves_rows_and_unique_indexes()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var original = Model(includePriority: false);
        Assert.True(connection.Schema.Apply(original).Applied);
        var session = connection.OpenSession(original, StorageAccess.Global);
        Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "one", ["value"] = "keep", ["uniqueValue"] = "unique"
        })).Status);

        var evolved = Model(includePriority: true);
        var applied = connection.Schema.Apply(evolved);
        Assert.True(applied.Applied);
        var read = connection.OpenSession(evolved, StorageAccess.Global).Read(new StorageKey(
            new Dictionary<string, object?> { ["id"] = "one" }));
        Assert.NotNull(read);
        Assert.Equal("keep", read!.Values.Values["value"]);
        Assert.Equal(0, read.Values.Values["priority"]);
        Assert.Equal(["by-value", "unique-value"], connection.Catalog.ReadIndexes(evolved.Id).Select(index => index.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(WriteOutcomeStatus.UniqueViolation, connection.OpenSession(evolved, StorageAccess.Global).Insert(
            new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = "two", ["value"] = "other", ["uniqueValue"] = "unique", ["priority"] = 1
            })).Status);
    }

    [Fact]
    public void Store_lock_is_held_for_connection_lifetime()
    {
        using var store = TemporaryStore.Create();
        using var first = new SqliteProviderFactory().Create(store.ConnectionString);
        var error = Assert.Throws<InvalidOperationException>(() => new SqliteProviderFactory().Create(store.ConnectionString));
        Assert.Contains("already in use", error.Message, StringComparison.Ordinal);
        first.Dispose();
        using var second = new SqliteProviderFactory().Create(store.ConnectionString);
    }

    [Fact]
    public void Batched_upserts_use_one_native_command_and_return_all_outcomes()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = Model(includePriority: false, includeUniqueIndex: false);
        connection.Schema.Apply(unit);
        var observer = new WritePathObserver();
        using var work = connection.BeginUnitOfWork(
            StorageAccess.Global,
            new BatchWriteOptions { MaxRowsPerFlush = 1_000, OutcomeMode = BatchOutcomeMode.Exact },
            unit);

        for (var index = 0; index < 1_000; index++)
        {
            work.Stage(RowWrite.Upsert(unit, new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = $"id-{index}",
                ["value"] = $"value-{index}",
                ["uniqueValue"] = $"unique-{index}"
            }), new WriteOptions { Observer = observer }));
        }

        var summary = work.CommitWithOutcomes();

        Assert.True(summary.IsSuccessful);
        Assert.Equal(1_000, summary.Submitted);
        Assert.Equal(1, observer.RoundTrips);
        Assert.Equal("value-999", connection.OpenSession(unit, StorageAccess.Global)
            .Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "id-999" }))!
            .Values.Values["value"]);
    }

    [Fact]
    public void Batched_insert_failure_reports_the_key_and_rolls_back_the_batch()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = Model(includePriority: false);
        connection.Schema.Apply(unit);
        using var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit);
        var first = RowWrite.Insert(unit, new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "one", ["value"] = "one", ["uniqueValue"] = "duplicate"
        }));
        var second = RowWrite.Insert(unit, new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "two", ["value"] = "two", ["uniqueValue"] = "duplicate"
        }));
        work.Stage(first);
        work.Stage(second);

        var error = Assert.Throws<BatchWriteException>(() => work.CommitWithOutcomes());
        Assert.Same(second, Assert.Single(error.Outcomes).Write);
        Assert.Contains("id=two", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(WriteOutcomeStatus.UniqueViolation), error.Message, StringComparison.Ordinal);
        Assert.Null(connection.OpenSession(unit, StorageAccess.Global)
            .Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "one" })));
    }

    [Fact]
    public void Batched_upserts_accept_heterogeneous_column_shapes()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = Model(includePriority: true, includeUniqueIndex: false);
        connection.Schema.Apply(unit);
        using var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit);
        work.Stage(RowWrite.Upsert(unit, new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "full", ["value"] = "full", ["uniqueValue"] = "full", ["priority"] = 7
        })));
        work.Stage(RowWrite.Upsert(unit, new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "partial", ["value"] = "partial", ["uniqueValue"] = "partial"
        })));

        var summary = work.CommitWithOutcomes();

        Assert.True(summary.IsSuccessful);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        Assert.Equal(7, session.Read(new StorageKey(new Dictionary<string, object?>
        {
            ["id"] = "full"
        }))!.Values.Values["priority"]);
        Assert.Equal(0, session.Read(new StorageKey(new Dictionary<string, object?>
        {
            ["id"] = "partial"
        }))!.Values.Values["priority"]);
    }

    [Fact]
    public void Exact_batched_upserts_return_optimistic_versions()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = Model(includePriority: false, includeUniqueIndex: false) with
        {
            Id = new StorageUnitId("batched-versions"),
            Name = "batched_versions",
            Concurrency = ConcurrencyDeclaration.Optimistic()
        };
        connection.Schema.Apply(unit);

        using (var first = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit))
        {
            first.Stage(RowWrite.Upsert(unit, new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = "same", ["value"] = "first", ["uniqueValue"] = "first"
            })));
            Assert.Equal(1, first.CommitWithOutcomes().Outcomes.Single().Outcome.Version);
        }

        using var second = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit);
        second.Stage(RowWrite.Upsert(unit, new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "same", ["value"] = "second", ["uniqueValue"] = "second"
        })));
        Assert.Equal(2, second.CommitWithOutcomes().Outcomes.Single().Outcome.Version);
    }

    [Fact]
    public void Batched_upserts_chunk_at_the_sqlite_variable_limit()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var columns = Enumerable.Range(0, 39)
            .Select(index => new ColumnDefinition { Name = $"value{index}", Type = PortableType.String })
            .Prepend(new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false })
            .ToArray();
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("batched-wide"),
            Name = "batched_wide",
            Columns = columns,
            Key = new KeyDefinition { Columns = ["id"] }
        };
        connection.Schema.Apply(unit);
        var observer = new WritePathObserver();
        using var work = connection.BeginUnitOfWork(
            StorageAccess.Global,
            new BatchWriteOptions { MaxRowsPerFlush = 1_000, OutcomeMode = BatchOutcomeMode.Exact },
            unit);
        for (var row = 0; row < 1_000; row++)
        {
            var values = columns.ToDictionary(
                column => column.Name,
                column => (object?)$"{column.Name}-{row}",
                StringComparer.Ordinal);
            work.Stage(RowWrite.Upsert(unit, new StorageValues(values), new WriteOptions { Observer = observer }));
        }

        var report = work.CommitWithOutcomes();

        Assert.Equal(1_000, report.Succeeded);
        Assert.Equal(2, observer.RoundTrips);
    }

    private static StorageUnit Model(bool includePriority, bool includeUniqueIndex = true) => new()
    {
        Id = new StorageUnitId("rebuild"), Name = "rebuild",
        Columns = includePriority
            ? [new() { Name = "id", Type = PortableType.String, IsNullable = false }, new() { Name = "value", Type = PortableType.String }, new() { Name = "uniqueValue", Type = PortableType.String }, new() { Name = "priority", Type = PortableType.Int32, IsNullable = false, Default = new PortableDefault(0) }]
            : [new() { Name = "id", Type = PortableType.String, IsNullable = false }, new() { Name = "value", Type = PortableType.String }, new() { Name = "uniqueValue", Type = PortableType.String }],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes = includeUniqueIndex
            ?
            [
                new IndexDefinition { Name = "by-value", Columns = [new IndexColumn("value")] },
                new IndexDefinition { Name = "unique-value", Columns = [new IndexColumn("uniqueValue")], IsUnique = true }
            ]
            : [new IndexDefinition { Name = "by-value", Columns = [new IndexColumn("value")] }]
    };

    private sealed class TemporaryStore : IDisposable
    {
        private readonly string directory;
        private TemporaryStore(string directory) { this.directory = directory; ConnectionString = $"Data Source={Path.Combine(directory, "store.db")}"; }
        public string ConnectionString { get; }
        public static TemporaryStore Create() { var path = Path.Combine(Path.GetTempPath(), "groundwork-sqlite-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return new(path); }
        public void Dispose() { try { Directory.Delete(directory, recursive: true); } catch { } }
    }
}
