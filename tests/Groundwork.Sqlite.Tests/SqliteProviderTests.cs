using Microsoft.Data.Sqlite;
using Groundwork.Kernel;
using Groundwork.Testing;
using Groundwork.Sqlite;
using Groundwork.Query.Linq;
using Groundwork.Query.Model;
using Groundwork.Query.Linq.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteProviderTests
{
    [Fact]
    public void Native_aggregation_preserves_separator_values_and_independent_FirstBy_orders()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = AggregationUnit(
            "aggregation-adversarial",
            [
                new Aggregate.SetUnion("labels", "label", 8),
                new Aggregate.FirstBy("firstAscending", "label", "ascendingOrder"),
                new Aggregate.FirstBy("firstDescending", "label", "descendingOrder", SortDirection.Descending)
            ]);
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        InsertAggregationRow(session, "1", "g", 0, 0m, "a\u001fb", 1, 1);
        InsertAggregationRow(session, "2", "g", 0, 0m, "plain", 2, 3);
        InsertAggregationRow(session, "3", "g", 0, 0m, "last", 3, 2);

        var row = Assert.Single(session.Aggregate(new AggregationQuery("summary")).Rows);

        Assert.Equal(new[] { "a\u001fb", "last", "plain" }, Assert.IsType<string[]>(row["labels"]));
        Assert.Equal("a\u001fb", row["firstAscending"]);
        Assert.Equal("plain", row["firstDescending"]);
    }

    [Fact]
    public void Native_aggregation_sums_decimal_text_exactly_and_widens_Int32()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = AggregationUnit(
            "aggregation-sums",
            [new Aggregate.Sum("integerTotal", "integerAmount"), new Aggregate.Sum("decimalTotal", "decimalAmount")]);
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        InsertAggregationRow(session, "1", "g", 2_000_000_000, 12_345_678_901_234_567_890.1234m, "a", 1, 1);
        InsertAggregationRow(session, "2", "g", 2_000_000_000, 0.0001m, "b", 2, 2);

        var row = Assert.Single(session.Aggregate(new AggregationQuery("summary")).Rows);

        Assert.Equal(4_000_000_000L, Assert.IsType<long>(row["integerTotal"]));
        Assert.Equal(12_345_678_901_234_567_890.1235m, Assert.IsType<decimal>(row["decimalTotal"]));
    }

    [Fact]
    public void Native_decimal_sum_reports_the_portable_overflow_refusal()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var template = AggregationUnit("aggregation-sum-overflow", [new Aggregate.Sum("decimalTotal", "decimalAmount")]);
        var unit = template with
        {
            Columns = template.Columns.Select(column => column.Name == "decimalAmount"
                ? column with { Precision = 29, Scale = 0 }
                : column).ToArray()
        };
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        InsertAggregationRow(session, "1", "g", 0, decimal.MaxValue, "a", 1, 1);
        InsertAggregationRow(session, "2", "g", 0, decimal.MaxValue, "b", 2, 2);

        var exception = Assert.Throws<AggregationBudgetExceededException>(() =>
            session.Aggregate(new AggregationQuery("summary")));

        Assert.Equal("GW-AGG-SUM-001", exception.Code);
    }

    [Fact]
    public void Native_SetUnion_uses_ordinal_identity_even_for_NOCASE_storage()
    {
        using var store = TemporaryStore.Create();
        var unit = AggregationUnit("aggregation-ordinal-set", [new Aggregate.SetUnion("labels", "label", 2)]) with
        {
            Columns = AggregationUnit("unused", []).Columns.Select(column => column.Name == "label"
                ? column with { Collation = PortableCollation.OrdinalIgnoreCase }
                : column).ToArray()
        };
        using (var native = new SqliteConnection(store.ConnectionString))
        {
            native.Open();
            using var command = native.CreateCommand();
            command.CommandText = """
                CREATE TABLE aggregation_ordinal_set (
                    id TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
                    "group" TEXT COLLATE BINARY NOT NULL,
                    integerAmount INTEGER NOT NULL,
                    decimalAmount TEXT NOT NULL,
                    label TEXT COLLATE NOCASE NULL,
                    ascendingOrder INTEGER NOT NULL,
                    descendingOrder INTEGER NOT NULL,
                    __groundwork_action TEXT NOT NULL DEFAULT 'I');
                """;
            command.ExecuteNonQuery();
        }
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        InsertAggregationRow(session, "1", "g", 0, 0m, "A", 1, 1);
        InsertAggregationRow(session, "2", "g", 0, 0m, "a", 2, 2);

        var row = Assert.Single(session.Aggregate(new AggregationQuery("summary")).Rows);

        Assert.Equal(new[] { "A", "a" }, Assert.IsType<string[]>(row["labels"]));
    }

    [Theory]
    [InlineData("input", "GW-AGG-BOUND-004")]
    [InlineData("groups", "GW-AGG-BOUND-005")]
    [InlineData("values", "GW-AGG-BOUND-007")]
    public void Native_aggregation_refuses_each_budget_without_truncating(
        string budget,
        string expectedCode)
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var template = AggregationUnit(
            "aggregation-budget-" + budget,
            [new Aggregate.SetUnion("labels", "label", budget == "values" ? 1 : 8)]);
        var unit = template with
        {
            AggregationProfiles =
            [
                template.AggregationProfiles.Single() with
                {
                    MaxInputRows = budget == "input" ? 1 : 8,
                    MaxGroups = budget == "groups" ? 1 : 8
                }
            ]
        };
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        InsertAggregationRow(session, "1", "a", 0, 0m, "x", 1, 1);
        InsertAggregationRow(session, "2", budget == "groups" ? "b" : "a", 0, 0m, "y", 2, 2);

        var exception = Assert.Throws<AggregationBudgetExceededException>(() =>
            session.Aggregate(new AggregationQuery("summary")));

        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public void Declared_aggregation_profile_executes_as_a_bounded_native_reduction()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("aggregation-sqlite"),
            Name = "aggregation_sqlite",
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "group", Type = PortableType.String, IsNullable = false },
                new() { Name = "amount", Type = PortableType.Int64 },
                new() { Name = "label", Type = PortableType.String },
                new() { Name = "order", Type = PortableType.Int64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            AggregationProfiles =
            [
                new AggregationProfile
                {
                    Name = "summary",
                    GroupByColumns = ["group"],
                    Aggregates =
                    [
                        new Aggregate.Min("minimum", "amount"),
                        new Aggregate.Max("maximum", "amount"),
                        new Aggregate.Sum("total", "amount"),
                        new Aggregate.SetUnion("labels", "label", 4),
                        new Aggregate.FirstBy("first", "label", "order")
                    ],
                    MaxGroups = 4,
                    MaxInputRows = 8
                }
            ]
        };
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        Assert.Equal(PortableType.Int64, session.Unit.Columns.Single(column => column.Name == "amount").Type);
        Assert.Equal("amount", Assert.IsType<Aggregate.Sum>(session.Unit.AggregationProfiles.Single().Aggregates.Single(item => item.Alias == "total")).Column);
        foreach (var row in new[]
        {
            (Id: "1", Group: "a", Amount: (long?)3, Label: "x", Order: 2L),
            (Id: "2", Group: "a", Amount: (long?)null, Label: "y", Order: 1L),
            (Id: "3", Group: "b", Amount: (long?)7, Label: null, Order: 3L),
            (Id: "4", Group: "c", Amount: (long?)null, Label: "z", Order: 4L)
        })
            Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = row.Id, ["group"] = row.Group, ["amount"] = row.Amount, ["label"] = row.Label, ["order"] = row.Order
            })).Status);

        var result = session.Aggregate(new AggregationQuery("summary"));

        Assert.Equal(3, result.Rows.Count);
        var a = Assert.Single(result.Rows, item => Equals(item["group"], "a"));
        Assert.Equal(3L, Assert.IsType<long>(a["total"]));
        Assert.Equal(new[] { "x", "y" }, Assert.IsType<string[]>(a["labels"]));
        Assert.Equal("y", a["first"]);
        var c = Assert.Single(result.Rows, item => Equals(item["group"], "c"));
        Assert.Null(c["total"]);
    }

    private static StorageUnit AggregationUnit(string identity, IReadOnlyList<Aggregate> aggregates) => new()
    {
        Id = new StorageUnitId(identity),
        Name = identity.Replace('-', '_'),
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, IsNullable = false },
            new() { Name = "group", Type = PortableType.String, IsNullable = false },
            new() { Name = "integerAmount", Type = PortableType.Int32, IsNullable = false },
            new() { Name = "decimalAmount", Type = PortableType.Decimal, Precision = 28, Scale = 4, IsNullable = false },
            new() { Name = "label", Type = PortableType.String },
            new() { Name = "ascendingOrder", Type = PortableType.Int64, IsNullable = false },
            new() { Name = "descendingOrder", Type = PortableType.Int64, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        AggregationProfiles =
        [
            new AggregationProfile
            {
                Name = "summary",
                GroupByColumns = ["group"],
                Aggregates = aggregates,
                MaxGroups = 8,
                MaxInputRows = 16
            }
        ]
    };

    private static void InsertAggregationRow(
        IStorageSession session,
        string id,
        string group,
        int integerAmount,
        decimal decimalAmount,
        string? label,
        long ascendingOrder,
        long descendingOrder) =>
        Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["group"] = group,
            ["integerAmount"] = integerAmount,
            ["decimalAmount"] = decimalAmount,
            ["label"] = label,
            ["ascendingOrder"] = ascendingOrder,
            ["descendingOrder"] = descendingOrder
        })).Status);

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
