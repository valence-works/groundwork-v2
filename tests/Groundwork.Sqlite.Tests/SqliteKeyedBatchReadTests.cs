using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Sqlite;
using Groundwork.Store;
using Xunit;

namespace Groundwork.Sqlite.Tests;

/// <summary>
/// Conformance evidence that the keyed batch-read primitive chunks under SQLite's real 999-parameter
/// budget rather than SQLite's own driver refusing an over-budget statement. SQLite is the tightest
/// budget of the four providers, so a key set here that would fail unchunked with `SQLITE_ERROR`
/// (too many SQL variables) is the sharpest proof the chunking is real rather than incidental.
/// </summary>
public sealed class SqliteKeyedBatchReadTests
{
    [Fact]
    public void A_key_set_far_beyond_the_999_parameter_budget_is_chunked_under_the_real_connection_budget()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = CreateUnit("gw-batch-read");
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);

        const int rowCount = 2_500;
        for (var id = 1; id <= rowCount; id++)
        {
            session.Insert(new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = (long)id,
                ["owner"] = "owner-" + id
            }));
        }

        var idColumn = new ColumnRef("id", QueryType.Int64, isNullable: false);
        var keys = Enumerable.Range(1, rowCount).Select(id => (object?)(long)id).ToArray();
        var request = new KeyedBatchReadRequest(new TableId(unit.Name), idColumn, keys);

        // Passing the connection is what makes this SQLite's real 999-parameter budget rather than
        // the portable 1,000-value default — a request chunked at 1,000 would still overflow SQLite's
        // real bind-parameter ceiling by one and fail with a driver error, not a Groundwork refusal.
        var result = session.BatchRead(request, connection);

        Assert.Equal(rowCount, result.Rows.Count);
        Assert.Empty(result.MissingKeys);
        Assert.Equal(
            Enumerable.Range(1, rowCount).Select(id => (long)id),
            result.Rows.Select(row => (long)row.Values["id"]!));
    }

    [Fact]
    public void A_scoped_batch_read_reserves_the_scope_parameter_under_sqlites_999_budget()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = CreateUnit("gw-batch-read-scoped") with { Scope = ScopePolicy.Scoped };
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("tenant-a")));

        const int rowCount = 2_500;
        for (var id = 1; id <= rowCount; id++)
        {
            session.Insert(new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = (long)id,
                ["owner"] = "owner-" + id
            }));
        }

        var idColumn = new ColumnRef("id", QueryType.Int64, isNullable: false);
        var keys = Enumerable.Range(1, rowCount).Select(id => (object?)(long)id).ToArray();
        var result = session.BatchRead(
            new KeyedBatchReadRequest(new TableId(unit.Name), idColumn, keys),
            connection);

        Assert.Equal(rowCount, result.Rows.Count);
        Assert.Empty(result.MissingKeys);
    }

    [Fact]
    public void A_folded_key_matches_once_and_reports_the_first_requested_spelling()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var plain = CreateUnit("gw-batch-read-folded");
        var unit = plain with
        {
            Columns = plain.Columns.Select(column => column.Name == "owner"
                ? column with { Collation = PortableCollation.OrdinalIgnoreCase }
                : column).ToArray()
        };
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = 1L,
            ["owner"] = "foo"
        }));

        var owner = new ColumnRef(
            new TableId(unit.Name),
            "owner",
            QueryType.String,
            isNullable: false,
            maxLength: 32,
            stringComparison: QueryStringComparisonPolicy.AsciiIgnoreCase);
        var result = session.BatchRead(
            new KeyedBatchReadRequest(
                new TableId(unit.Name),
                owner,
                new object?[] { "FOO", "foo", "missing" }),
            connection);

        var row = Assert.Single(result.Rows);
        Assert.Equal("FOO", row.Key.Value);
        Assert.Equal("foo", row.Values["owner"]);
        Assert.Equal(new[] { "missing" }, result.MissingKeys.Select(key => key.Value));
    }

    private static StorageUnit CreateUnit(string id) => new()
    {
        Id = new StorageUnitId(id),
        Name = id.Replace('-', '_'),
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.Int64, IsNullable = false },
            new ColumnDefinition { Name = "owner", Type = PortableType.String, IsNullable = false, MaxLength = 32 }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private sealed class TemporaryStore : IDisposable
    {
        private readonly string directory;

        private TemporaryStore(string directory)
        {
            this.directory = directory;
            ConnectionString = $"Data Source={Path.Combine(directory, "store.db")}";
        }

        public string ConnectionString { get; }

        public static TemporaryStore Create()
        {
            var path = Path.Combine(Path.GetTempPath(), "groundwork-sqlite-batch-read-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
            }
        }
    }
}
