using Microsoft.Data.Sqlite;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Testing;
using Groundwork.Sqlite;
using Groundwork.Query.Linq;
using Groundwork.Query.Model;
using Groundwork.Query.Linq.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteProviderTests
{
    private sealed class LinqTicket
    {
        public string Id { get; set; } = string.Empty;
        public string Display { get; set; } = string.Empty;
        public string Code = string.Empty;
    }

    [Fact]
    public async Task Configured_linq_database_executes_ToListAsync_against_sqlite()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("linq-tickets"), Name = "linq-tickets",
            Columns = [new() { Name = "Id", Type = PortableType.String, IsNullable = false }, new() { Name = "value_col", Type = PortableType.String }, new() { Name = "code_col", Type = PortableType.String }],
            Key = new KeyDefinition { Columns = ["Id"] }
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(new Dictionary<string, object?> { ["Id"] = "a", ["value_col"] = "hit", ["code_col"] = "C1" })).Status);

        var query = new GwQueryDatabase(new SqliteLinqExecutor(session)).Table<LinqTicket>(
            new GwTableModel<LinqTicket>("linq-tickets", [
                new GwColumn<LinqTicket>(nameof(LinqTicket.Id), "Id", QueryType.String, false),
                new GwColumn<LinqTicket>(nameof(LinqTicket.Display), "value_col", QueryType.String),
                new GwColumn<LinqTicket>(nameof(LinqTicket.Code), "code_col", QueryType.String)
            ])).Where(ticket => ticket.Display == "hit");
        var rows = await query.ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("a", row.Id);
        Assert.Equal("hit", row.Display);
        Assert.Equal("C1", row.Code);
        await Assert.ThrowsAsync<InvalidOperationException>(() => query.Select(ticket => new { ticket.Id }).ToListAsync());
    }

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
    public void Folded_schema_migration_backfills_and_partial_updates_preserve_the_key()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var original = new StorageUnit
        {
            Id = new StorageUnitId("folded-migration"),
            Name = "folded_migration",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new ColumnDefinition { Name = "status", Type = PortableType.String, MaxLength = 32, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
        Assert.True(connection.Schema.Apply(original).Applied);
        Assert.Equal(WriteOutcomeStatus.Inserted, connection.OpenSession(original, StorageAccess.Global)
            .Insert(new StorageValues(new Dictionary<string, object?> { ["id"] = 1, ["status"] = "Open" })).Status);

        var folded = original with
        {
            Columns = [.. original.Columns.Select(column => column.Name == "status"
                ? column with { Collation = PortableCollation.OrdinalIgnoreCase }
                : column)],
            Indexes = [new IndexDefinition { Name = "by-status", Columns = [new IndexColumn("status")] }]
        };
        Assert.Contains(SearchKeyProjection.Expand(folded).Columns, column => column.Name == "__groundwork_search_status");
        var foldedDiff = connection.Schema.Diff(folded);
        using (var historyConnection = new SqliteConnection(store.ConnectionString))
        {
            historyConnection.Open();
            using var historyCommand = historyConnection.CreateCommand();
            historyCommand.CommandText = "SELECT state_json FROM __groundwork_schema_history WHERE subject_id='folded-migration'";
            var state = PhysicalSchemaAppliedStateSerializer.Deserialize((string)historyCommand.ExecuteScalar()!);
            var target = SqliteSchemaCoordinator.Target(SqliteSchemaCoordinator.Physicalize(folded));
            var plan = PhysicalSchemaDiffPlanner.Plan(
                target,
                PhysicalSchemaHistoryState.FromApplied(state),
                DateTimeOffset.UnixEpoch);
            Assert.True(plan.IsApplicable, string.Join("; ", plan.Refusals.Select(refusal => refusal.Code + ":" + refusal.Message)));
            Assert.Contains(plan.Operations, operation => operation is BackfillColumnOperation backfill &&
                backfill.Derived is not null && backfill.RequiresAuthorization);
            Assert.Contains(plan.Operations, operation => operation is FinalizeColumnOperation finalize &&
                finalize.Column.Name == SearchKeyProjection.ColumnName("status"));
        }
        var foldedApply = connection.Schema.Apply(folded);
        Assert.True(foldedApply.Applied, string.Join("; ", foldedDiff.Changes.Select(change => change.Kind + ":" + change.Identity)) + " / " + string.Join("; ", foldedApply.Diff.Changes.Select(change => change.Kind + ":" + change.Identity)));

        var status = new ColumnRef(
            new TableId(folded.Name), "status", Groundwork.Query.Model.QueryType.String, false, 32,
            stringComparison: Groundwork.Query.Model.QueryStringComparisonPolicy.AsciiIgnoreCase);
        var session = connection.OpenSession(folded, StorageAccess.Global);
        var stored = session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = 1 }));
        Assert.NotNull(stored);
        Assert.DoesNotContain(SearchKeyProjection.ColumnName("status"), stored!.Values.Values.Keys);
        var result = session.Query(new Groundwork.Query.Model.QueryRequest(
            new Groundwork.Query.Model.TableId(folded.Name),
            new Groundwork.Query.Model.Predicate.StartsWith(status, "OP"),
            [], Groundwork.Query.Model.Projection.All, Groundwork.Query.Model.Paging.None));
        Assert.Equal([1], result.Rows.Select(row => Assert.IsType<int>(row["id"])));

        var indexed = session.Query(new Groundwork.Query.Model.QueryRequest(
            new Groundwork.Query.Model.TableId(folded.Name),
            new Groundwork.Query.Model.Predicate.StartsWith(status, "OP"),
            [], Groundwork.Query.Model.Projection.All, Groundwork.Query.Model.Paging.None),
            new QueryRenderOptions(
                [new QueryIndexDeclaration("by-status", [new QueryIndexColumn("status", false, QueryType.String)], QueryIndexPinning.Pinned)],
                selectedIndex: "by-status"));
        Assert.Equal("by-status", indexed.SelectedIndex);
        Assert.Equal([1], indexed.Rows.Select(row => Assert.IsType<int>(row["id"])));

        Assert.Equal(WriteOutcomeStatus.Updated, session.Update(new StorageValues(new Dictionary<string, object?> { ["id"] = 1 })).Status);
        Assert.Single(session.Query(new Groundwork.Query.Model.QueryRequest(
            new Groundwork.Query.Model.TableId(folded.Name),
            new Groundwork.Query.Model.Predicate.StartsWith(status, "OP"),
            [], Groundwork.Query.Model.Projection.All, Groundwork.Query.Model.Paging.None)).Rows);

        using var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, folded);
        work.Stage(RowWrite.Update(folded, new StorageValues(new Dictionary<string, object?> { ["id"] = 1 })));
        Assert.True(work.CommitWithOutcomes().IsSuccessful);
        Assert.Single(session.Query(new Groundwork.Query.Model.QueryRequest(
            new Groundwork.Query.Model.TableId(folded.Name),
            new Groundwork.Query.Model.Predicate.StartsWith(status, "OP"),
            [], Groundwork.Query.Model.Projection.All, Groundwork.Query.Model.Paging.None)).Rows);

        using (var tamper = new SqliteConnection(store.ConnectionString))
        {
            tamper.Open();
            using var command = tamper.CreateCommand();
            command.CommandText = "UPDATE __groundwork_search_key_algorithms SET algorithm_id='stale-search-key-v0' WHERE table_name='folded_migration' AND column_name='__groundwork_search_status';";
            command.ExecuteNonQuery();
        }
        var admission = Assert.Throws<InvalidOperationException>(() => connection.OpenSession(folded, StorageAccess.Global));
        Assert.Contains("rebuild", admission.Message, StringComparison.OrdinalIgnoreCase);
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
