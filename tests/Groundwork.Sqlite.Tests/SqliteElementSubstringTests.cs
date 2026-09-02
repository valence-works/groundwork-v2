using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Sqlite;
using Groundwork.Store;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteElementSubstringTests
{
    [Fact]
    public void Unicode_element_substring_is_server_evaluated_and_hides_the_parallel_key_array()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("sqlite-element-substring"),
            Name = "element_substring_rows",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new ColumnDefinition
                {
                    Name = "workflowIds",
                    Type = PortableType.Json,
                    ElementSearchKey = new ElementSearchKeyDefinition
                    {
                        Collation = PortableCollation.UnicodeOrdinalIgnoreCase,
                        MaximumElementCodeUnits = 450
                    }
                }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };

        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Insert(Values(1, new[] { "WORKFLOW" }));
        session.Insert(Values(2, new[] { "WÖRKFLOW" }));
        session.Insert(Values(3, new[] { "not-a-match" }));
        session.Insert(Values(4, new object?[] { "wö", "rk" }));
        session.Insert(Values(5, new object?[] { 42, null }));
        session.Insert(Values(6, Array.Empty<string>()));
        session.Insert(new StorageValues(new Dictionary<string, object?> { ["id"] = 7, ["workflowIds"] = "{}" }));
        session.Insert(new StorageValues(new Dictionary<string, object?> { ["id"] = 8 }));

        var request = new QueryRequest(
            new TableId(unit.Name),
            new Predicate.ElementSubstring(
                new ElementSetRef("workflowIds", QueryType.String),
                "wörk",
                Anchor.Contains,
                QueryStringComparisonPolicy.UnicodeOrdinalIgnoreCase),
            [],
            Projection.All,
            Paging.None,
            ResultShape.Rows.Instance,
            acceptedScan: ScanAcceptance.Allow(
                "GW-SCAN-371",
                "Unicode element substring uses the declared persisted key array; bounded scan is intentional.",
                "groundwork-tests",
                DateTimeOffset.UtcNow.AddDays(1)));

        var observer = new ProviderCommandObserver();
        var observedSession = connection.OpenSession(unit, StorageAccess.Global, observer);
        var result = observedSession.Query(request);

        Assert.Equal([2], result.Rows.Select(row => Assert.IsType<int>(row["id"])));
        Assert.All(result.Rows, row => Assert.DoesNotContain(SearchKeyProjection.Prefix, row.Keys));
        var command = Assert.Single(observer.Commands);
        Assert.Equal("sqlite.query", command.Operation);
        Assert.Contains("__groundwork_search_workflowIds", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("json_each", command.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EXISTS", command.CommandText, StringComparison.OrdinalIgnoreCase);

        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection(store.ConnectionString))
        {
            raw.Open();
            raw.CreateCollation("GROUNDWORK_UTF16_ORDINAL", string.CompareOrdinal);
            using var explain = raw.CreateCommand();
            explain.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText!.TrimEnd().TrimEnd(';');
            explain.Parameters.AddWithValue("@p0", "plan-only");
            using var reader = explain.ExecuteReader();
            var plan = new List<string>();
            while (reader.Read())
                plan.Add(reader.GetString(3));
            Assert.Contains(plan, detail => detail.Contains("json_each", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(plan, detail => detail.Contains("VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase));
        }

        var stored = session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = 2 }));
        Assert.NotNull(stored);
        Assert.DoesNotContain(SearchKeyProjection.Prefix, stored!.Values.Values.Keys);
    }

    private static StorageValues Values(int id, object value) => new(new Dictionary<string, object?>
    {
        ["id"] = id,
        ["workflowIds"] = JsonSerializer.Serialize(value)
    });

    private sealed class TemporaryStore : IDisposable
    {
        private readonly string directory;

        private TemporaryStore(string directory)
        {
            this.directory = directory;
            ConnectionString = $"Data Source={Path.Combine(directory, "element-substring.db")}";
        }

        public string ConnectionString { get; }

        public static TemporaryStore Create()
        {
            var path = Path.Combine(Path.GetTempPath(), "groundwork-sqlite-element-substring-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryStore(path);
        }

        public void Dispose()
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString);
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
            Directory.Delete(directory, recursive: true);
        }
    }
}
